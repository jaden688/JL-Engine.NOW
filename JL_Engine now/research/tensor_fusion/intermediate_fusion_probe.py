#!/usr/bin/env python3
"""
JL intermediate-layer tensor-fusion probe.

Unlike the Stage-0 final-state probe, this experiment merges two real hidden
tensors inside model A and then forces the composite state through multiple
frozen nonlinear transformer blocks before decoding.

Both foundation models stay frozen. Fusion strategy and alpha are chosen on
stratified calibration blocks only. Evaluation uses untouched stratified blocks
from different files, a same-path shuffled-B control, a scaled-A control,
paired block bootstrap intervals, an exact paired sign-flip test, and a
gradient influence check on both injected tensors.
"""

from __future__ import annotations

import argparse
import json
import time
from collections import defaultdict
from pathlib import Path

import torch
import torch.nn.functional as F

from tensor_fusion_probe import (
    DEFAULT_BOOTSTRAP_SAMPLES,
    DEFAULT_MODEL_A,
    DEFAULT_MODEL_B,
    TensorBridge,
    alpha_from_key,
    fit_procrustes,
    iter_batches,
    load_document_set,
    load_frozen_model,
    map_hidden,
    mean_cosine,
    paired_block_bootstrap,
    record_score,
    render_document_set,
    resolve_device,
    serialize_losses,
    sha256_text,
    shuffled_like,
    stratified_token_blocks,
    verify_tokenizer_compatibility,
)


DEFAULT_ALPHAS = (0.25, 0.5, 0.75)
CALIBRATION_FILES = (
    "data/agents/Slappy_Full.json",
    "data/JLframe_Engine_Framework.json",
    "data/behavior_states.json",
    "dotnet/JLEngine.Core/Types/Types.cs",
)
EVALUATION_FILES = (
    "data/agents/SparkByte_Full.json",
    "dotnet/JLEngine.Core/Engine/JLEngineCore.cs",
    "dotnet/JLEngine.Runtime/AgentRuntime.cs",
    "dotnet/JLEngine.Runtime/Tools/ToolRegistry.cs",
)


def parse_args() -> argparse.Namespace:
    project_root = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project-root", type=Path, default=project_root)
    parser.add_argument("--model-a", default=DEFAULT_MODEL_A)
    parser.add_argument("--model-b", default=DEFAULT_MODEL_B)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--sequence-length", type=int, default=128)
    parser.add_argument("--batch-size", type=int, default=2)
    parser.add_argument("--calibration-tokens", type=int, default=3072)
    parser.add_argument("--evaluation-tokens", type=int, default=2048)
    parser.add_argument(
        "--model-a-block-index",
        type=int,
        default=5,
        help="Zero-based model-A block whose output is replaced by the fusion.",
    )
    parser.add_argument(
        "--model-b-hidden-index",
        type=int,
        default=3,
        help=(
            "Index into model B's output_hidden_states tuple. Index 3 is the "
            "output after DistilGPT2 block 3."
        ),
    )
    parser.add_argument(
        "--bootstrap-samples",
        type=int,
        default=DEFAULT_BOOTSTRAP_SAMPLES,
    )
    parser.add_argument(
        "--allow-download",
        action="store_true",
        help="Allow Hugging Face downloads. Default is cache-only/offline.",
    )
    parser.add_argument("--json-output", type=Path)
    return parser.parse_args()


@torch.inference_mode()
def collect_hidden_at(
    model: torch.nn.Module,
    blocks: torch.Tensor,
    hidden_index: int,
    batch_size: int,
    device: torch.device,
) -> torch.Tensor:
    rows: list[torch.Tensor] = []
    for cpu_batch in iter_batches(blocks, batch_size):
        output = model(
            input_ids=cpu_batch.to(device),
            output_hidden_states=True,
            use_cache=False,
        )
        hidden = output.hidden_states[hidden_index][:, :-1, :]
        rows.append(hidden.detach().float().cpu().reshape(-1, hidden.shape[-1]))
    return torch.cat(rows, dim=0)


def mid_partner(
    key: str,
    hidden_b: torch.Tensor,
    mapped_b: torch.Tensor,
) -> torch.Tensor:
    if key.startswith("mid_raw_alpha_"):
        return hidden_b
    if key.startswith("mid_aligned_alpha_"):
        return mapped_b
    raise ValueError(f"Unknown intermediate-fusion strategy: {key}")


def choose_best_mid(losses: dict[str, float]) -> tuple[str, float, float]:
    candidates = {
        key: value
        for key, value in losses.items()
        if key.startswith(("mid_raw_alpha_", "mid_aligned_alpha_"))
    }
    key = min(candidates, key=candidates.get)
    return key, alpha_from_key(key), candidates[key]


def choose_best(losses: dict[str, float], prefix: str) -> tuple[str, float, float]:
    candidates = {
        key: value for key, value in losses.items() if key.startswith(prefix)
    }
    key = min(candidates, key=candidates.get)
    return key, alpha_from_key(key), candidates[key]


def model_a_with_injection(
    model_a: torch.nn.Module,
    input_ids: torch.Tensor,
    block_index: int,
    alpha: float,
    partner: torch.Tensor,
) -> torch.Tensor:
    def inject(
        _module: torch.nn.Module,
        _inputs: tuple[object, ...],
        output: torch.Tensor,
    ) -> torch.Tensor:
        if not isinstance(output, torch.Tensor):
            raise TypeError(
                "Expected the target GPT-2 block to return a tensor; "
                f"got {type(output)!r}."
            )
        fused = alpha * output.float() + (1.0 - alpha) * partner.float()
        return fused.to(output.dtype)

    handle = model_a.transformer.h[block_index].register_forward_hook(inject)
    try:
        output = model_a(
            input_ids=input_ids,
            output_hidden_states=False,
            use_cache=False,
        )
    finally:
        handle.remove()
    return output.logits[:, :-1, :].float()


def exact_paired_sign_flip(
    candidate: torch.Tensor,
    baseline: torch.Tensor,
) -> dict[str, object]:
    if candidate.shape != baseline.shape or candidate.ndim != 1:
        raise ValueError("Sign-flip inputs must be paired one-dimensional vectors.")
    delta = candidate.double() - baseline.double()
    block_count = delta.numel()
    if block_count > 20:
        raise ValueError("Exact sign-flip enumeration is capped at 20 blocks.")

    combinations = torch.arange(1 << block_count, dtype=torch.int64)
    bit_positions = torch.arange(block_count, dtype=torch.int64)
    signs = (
        ((combinations[:, None] >> bit_positions[None, :]) & 1).double()
        * 2.0
        - 1.0
    )
    null_means = (signs * delta.abs()[None, :]).mean(dim=1)
    observed = float(delta.mean())
    one_sided_p = float((null_means <= observed + 1e-12).double().mean())
    return {
        "alternative": "candidate mean NLL is lower",
        "observed_candidate_minus_baseline_mean_nll": observed,
        "exact_one_sided_p": one_sided_p,
        "enumerated_sign_patterns": int(1 << block_count),
        "paired_blocks": int(block_count),
    }


@torch.inference_mode()
def score_models(
    model_a: torch.nn.Module,
    model_b: torch.nn.Module,
    blocks: torch.Tensor,
    bridge: TensorBridge,
    model_a_block_index: int,
    model_b_hidden_index: int,
    alphas: tuple[float, ...],
    batch_size: int,
    device: torch.device,
    selected_mid_key: str | None = None,
    collect_block_losses: bool = False,
) -> tuple[
    dict[str, float],
    dict[str, float],
    dict[str, torch.Tensor],
]:
    totals: defaultdict[str, float] = defaultdict(float)
    block_losses: defaultdict[str, list[float]] = defaultdict(list)
    diagnostics: defaultdict[str, float] = defaultdict(float)
    token_count = 0
    batch_count = 0

    bridge_device = TensorBridge(
        source_mean=bridge.source_mean.to(device),
        target_mean=bridge.target_mean.to(device),
        rotation=bridge.rotation.to(device),
        singular_values=bridge.singular_values,
    )

    for cpu_batch in iter_batches(blocks, batch_size):
        batch = cpu_batch.to(device)
        labels = batch[:, 1:]
        output_a = model_a(
            input_ids=batch,
            output_hidden_states=True,
            use_cache=False,
        )
        output_b = model_b(
            input_ids=batch,
            output_hidden_states=True,
            use_cache=False,
        )

        logits_a = output_a.logits[:, :-1, :].float()
        logits_b = output_b.logits[:, :-1, :].float()
        hidden_b_mid = output_b.hidden_states[model_b_hidden_index].float()
        mapped_b_mid = map_hidden(hidden_b_mid, bridge_device)
        hidden_b_final = output_b.hidden_states[-1][:, :-1, :].float()
        model_b_through_a_head = model_a.lm_head(
            hidden_b_final.to(model_a.lm_head.weight.dtype)
        ).float()

        token_count += labels.numel()
        batch_count += 1
        record_score(
            totals,
            block_losses,
            "model_a",
            logits_a,
            labels,
            collect_block_losses,
        )
        record_score(
            totals,
            block_losses,
            "model_b",
            logits_b,
            labels,
            collect_block_losses,
        )

        mid_logits_by_key: dict[str, torch.Tensor] = {}
        for alpha in alphas:
            suffix = f"{alpha:.2f}"
            native_logit_fusion = alpha * logits_a + (1.0 - alpha) * logits_b
            record_score(
                totals,
                block_losses,
                f"native_logit_alpha_{suffix}",
                native_logit_fusion,
                labels,
                collect_block_losses,
            )

            final_same_head = (
                alpha * logits_a + (1.0 - alpha) * model_b_through_a_head
            )
            record_score(
                totals,
                block_losses,
                f"final_same_head_alpha_{suffix}",
                final_same_head,
                labels,
                collect_block_losses,
            )

            for strategy, partner in (
                ("raw", hidden_b_mid),
                ("aligned", mapped_b_mid),
            ):
                key = f"mid_{strategy}_alpha_{suffix}"
                injected_logits = model_a_with_injection(
                    model_a,
                    batch,
                    model_a_block_index,
                    alpha,
                    partner,
                )
                mid_logits_by_key[key] = injected_logits
                record_score(
                    totals,
                    block_losses,
                    key,
                    injected_logits,
                    labels,
                    collect_block_losses,
                )

        if selected_mid_key is not None:
            selected_alpha = alpha_from_key(selected_mid_key)
            selected_partner = mid_partner(
                selected_mid_key,
                hidden_b_mid,
                mapped_b_mid,
            )
            selected_logits = mid_logits_by_key[selected_mid_key]
            shuffled_logits = model_a_with_injection(
                model_a,
                batch,
                model_a_block_index,
                selected_alpha,
                shuffled_like(selected_partner),
            )
            record_score(
                totals,
                block_losses,
                "selected_mid_shuffled_b_control",
                shuffled_logits,
                labels,
                collect_block_losses,
            )

            scaled_a_logits = model_a_with_injection(
                model_a,
                batch,
                model_a_block_index,
                selected_alpha,
                torch.zeros_like(selected_partner),
            )
            record_score(
                totals,
                block_losses,
                "selected_mid_scaled_a_only_control",
                scaled_a_logits,
                labels,
                collect_block_losses,
            )

            final_same_head_selected = (
                selected_alpha * logits_a
                + (1.0 - selected_alpha) * model_b_through_a_head
            )
            diagnostics["selected_vs_final_linear_mean_abs_logit_delta"] += (
                float((selected_logits - final_same_head_selected).abs().mean())
            )
            diagnostics["selected_vs_model_a_mean_abs_logit_delta"] += float(
                (selected_logits - logits_a).abs().mean()
            )

        model_a_mid = output_a.hidden_states[model_a_block_index + 1]
        diagnostics["raw_mid_cosine"] += mean_cosine(
            model_a_mid,
            hidden_b_mid,
        )
        diagnostics["aligned_mid_cosine"] += mean_cosine(
            model_a_mid,
            mapped_b_mid,
        )
        diagnostics["model_a_mid_norm"] += float(
            model_a_mid.float().norm(dim=-1).mean()
        )
        diagnostics["model_b_mid_norm"] += float(
            hidden_b_mid.float().norm(dim=-1).mean()
        )
        diagnostics["mapped_b_mid_norm"] += float(
            mapped_b_mid.float().norm(dim=-1).mean()
        )

    losses = {name: total / token_count for name, total in totals.items()}
    averaged_diagnostics = {
        name: total / batch_count for name, total in diagnostics.items()
    }
    averaged_diagnostics["scored_tokens"] = float(token_count)
    return (
        losses,
        averaged_diagnostics,
        {
            name: torch.tensor(values, dtype=torch.float64)
            for name, values in block_losses.items()
        },
    )


def gradient_influence_probe(
    model_a: torch.nn.Module,
    model_b: torch.nn.Module,
    one_block: torch.Tensor,
    bridge: TensorBridge,
    selected_mid_key: str,
    model_a_block_index: int,
    model_b_hidden_index: int,
    device: torch.device,
) -> dict[str, object]:
    batch = one_block.to(device)
    with torch.no_grad():
        output_b = model_b(
            input_ids=batch,
            output_hidden_states=True,
            use_cache=False,
        )
        hidden_b = output_b.hidden_states[model_b_hidden_index].float()
        mapped_b = map_hidden(hidden_b, bridge)
        partner = mid_partner(selected_mid_key, hidden_b, mapped_b)

    alpha = alpha_from_key(selected_mid_key)
    leaves: dict[str, torch.Tensor] = {}

    def inject_with_leaves(
        _module: torch.nn.Module,
        _inputs: tuple[object, ...],
        output: torch.Tensor,
    ) -> torch.Tensor:
        model_a_leaf = output.detach().float().requires_grad_(True)
        model_b_leaf = partner.detach().float().requires_grad_(True)
        leaves["model_a_mid"] = model_a_leaf
        leaves["model_b_mid"] = model_b_leaf
        fused = alpha * model_a_leaf + (1.0 - alpha) * model_b_leaf
        return fused.to(output.dtype)

    model_a.zero_grad(set_to_none=True)
    handle = model_a.transformer.h[
        model_a_block_index
    ].register_forward_hook(inject_with_leaves)
    try:
        with torch.enable_grad():
            output = model_a(
                input_ids=batch,
                output_hidden_states=False,
                use_cache=False,
            )
            loss = F.cross_entropy(
                output.logits[:, :-1, :].float().reshape(
                    -1,
                    output.logits.shape[-1],
                ),
                batch[:, 1:].reshape(-1),
            )
            loss.backward()
    finally:
        handle.remove()

    base_parameter_gradients = sum(
        parameter.grad is not None for parameter in model_a.parameters()
    )
    result = {
        "loss": float(loss.detach()),
        "model_a_hidden_gradient_l2": float(
            leaves["model_a_mid"].grad.float().norm()
        ),
        "model_b_hidden_gradient_l2": float(
            leaves["model_b_mid"].grad.float().norm()
        ),
        "model_a_hidden_gradient_nonzero": bool(
            torch.count_nonzero(leaves["model_a_mid"].grad)
        ),
        "model_b_hidden_gradient_nonzero": bool(
            torch.count_nonzero(leaves["model_b_mid"].grad)
        ),
        "base_parameters_with_gradients": base_parameter_gradients,
    }
    model_a.zero_grad(set_to_none=True)
    return result


def paired_comparison(
    candidate: torch.Tensor,
    baseline: torch.Tensor,
    bootstrap_samples: int,
    seed: int,
) -> dict[str, object]:
    return {
        "bootstrap": paired_block_bootstrap(
            candidate,
            baseline,
            bootstrap_samples,
            seed,
        ),
        "exact_sign_flip": exact_paired_sign_flip(candidate, baseline),
    }


def run(args: argparse.Namespace) -> dict[str, object]:
    started = time.perf_counter()
    torch.manual_seed(688)
    device = resolve_device(args.device)
    if device.type == "cuda":
        torch.cuda.reset_peak_memory_stats()
        torch.backends.cuda.matmul.allow_tf32 = True

    calibration_documents = load_document_set(
        args.project_root,
        CALIBRATION_FILES,
    )
    evaluation_documents = load_document_set(
        args.project_root,
        EVALUATION_FILES,
    )
    calibration_text = render_document_set(calibration_documents)
    evaluation_text = render_document_set(evaluation_documents)

    tokenizer_a, model_a = load_frozen_model(
        args.model_a,
        device,
        args.allow_download,
    )
    tokenizer_b, model_b = load_frozen_model(
        args.model_b,
        device,
        args.allow_download,
    )
    verify_tokenizer_compatibility(tokenizer_a, tokenizer_b)

    if not 0 <= args.model_a_block_index < len(model_a.transformer.h):
        raise ValueError("model-a-block-index is outside model A's block stack.")
    if not 0 < args.model_b_hidden_index < len(model_b.transformer.h) + 1:
        raise ValueError("model-b-hidden-index is outside model B's hidden stack.")

    calibration_blocks, calibration_sampling = stratified_token_blocks(
        tokenizer_a,
        calibration_documents,
        args.calibration_tokens,
        args.sequence_length,
    )
    evaluation_blocks, evaluation_sampling = stratified_token_blocks(
        tokenizer_a,
        evaluation_documents,
        args.evaluation_tokens,
        args.sequence_length,
    )

    hidden_a_mid = collect_hidden_at(
        model_a,
        calibration_blocks,
        args.model_a_block_index + 1,
        args.batch_size,
        device,
    )
    hidden_b_mid = collect_hidden_at(
        model_b,
        calibration_blocks,
        args.model_b_hidden_index,
        args.batch_size,
        device,
    )
    bridge = fit_procrustes(hidden_b_mid, hidden_a_mid)

    calibration_losses, calibration_diagnostics, _ = score_models(
        model_a,
        model_b,
        calibration_blocks,
        bridge,
        args.model_a_block_index,
        args.model_b_hidden_index,
        DEFAULT_ALPHAS,
        args.batch_size,
        device,
    )
    selected_mid_key, selected_alpha, _ = choose_best_mid(calibration_losses)
    selected_native_logit_key, native_logit_alpha, _ = choose_best(
        calibration_losses,
        "native_logit_alpha_",
    )
    selected_final_linear_key, final_linear_alpha, _ = choose_best(
        calibration_losses,
        "final_same_head_alpha_",
    )

    (
        evaluation_losses,
        evaluation_diagnostics,
        evaluation_block_losses,
    ) = score_models(
        model_a,
        model_b,
        evaluation_blocks,
        bridge,
        args.model_a_block_index,
        args.model_b_hidden_index,
        DEFAULT_ALPHAS,
        args.batch_size,
        device,
        selected_mid_key=selected_mid_key,
        collect_block_losses=True,
    )

    best_single_key = min(
        ("model_a", "model_b"),
        key=evaluation_losses.get,
    )
    comparison_keys = {
        "mid_vs_best_single": best_single_key,
        "mid_vs_selected_native_logit": selected_native_logit_key,
        "mid_vs_selected_final_linear": selected_final_linear_key,
        "correct_pairing_vs_shuffled_b": "selected_mid_shuffled_b_control",
        "correct_pairing_vs_scaled_a_only": (
            "selected_mid_scaled_a_only_control"
        ),
    }
    comparisons = {
        name: paired_comparison(
            evaluation_block_losses[selected_mid_key],
            evaluation_block_losses[baseline_key],
            args.bootstrap_samples,
            seed=688 + index,
        )
        for index, (name, baseline_key) in enumerate(comparison_keys.items())
    }

    gradient_probe = gradient_influence_probe(
        model_a,
        model_b,
        evaluation_blocks[:1],
        bridge,
        selected_mid_key,
        args.model_a_block_index,
        args.model_b_hidden_index,
        device,
    )

    model_a_parameters = sum(parameter.numel() for parameter in model_a.parameters())
    model_b_parameters = sum(parameter.numel() for parameter in model_b.parameters())
    trainable_base_parameters = sum(
        parameter.numel()
        for model in (model_a, model_b)
        for parameter in model.parameters()
        if parameter.requires_grad
    )

    selected_loss = evaluation_losses[selected_mid_key]
    best_single_loss = evaluation_losses[best_single_key]
    native_logit_loss = evaluation_losses[selected_native_logit_key]
    final_linear_loss = evaluation_losses[selected_final_linear_key]
    shuffled_loss = evaluation_losses["selected_mid_shuffled_b_control"]
    both_models_contributed = 0.0 < selected_alpha < 1.0
    nonlinear_output_changed = (
        evaluation_diagnostics[
            "selected_vs_final_linear_mean_abs_logit_delta"
        ]
        > 1e-5
    )
    causal_pairing_signal = selected_loss < shuffled_loss
    gradients_reach_both = (
        gradient_probe["model_a_hidden_gradient_nonzero"]
        and gradient_probe["model_b_hidden_gradient_nonzero"]
        and gradient_probe["base_parameters_with_gradients"] == 0
    )
    useful_mid_stack = selected_loss < min(
        best_single_loss,
        native_logit_loss,
        final_linear_loss,
    )
    confirmatory_names = (
        "mid_vs_best_single",
        "mid_vs_selected_native_logit",
        "mid_vs_selected_final_linear",
    )
    statistically_supported = useful_mid_stack and all(
        comparisons[name]["bootstrap"]["ci95_high"] < 0.0
        and comparisons[name]["exact_sign_flip"]["exact_one_sided_p"]
        < (0.05 / len(confirmatory_names))
        for name in confirmatory_names
    )

    elapsed = time.perf_counter() - started
    return {
        "experiment": "jl-hidden-tensor-fusion-intermediate-stage-1",
        "claim_boundary": {
            "mechanism": (
                "Two independently executed frozen models supplied real "
                "intermediate tensors. Their composite was injected after "
                f"model A block {args.model_a_block_index + 1} and processed "
                "through the rest of model A's frozen nonlinear stack."
            ),
            "not_claimed": (
                "This does not merge foundation weights, create a literal sum "
                "of parameter counts, establish heterogeneous proprietary-model "
                "compatibility, or demonstrate broad capability gains. GPT-2 "
                "and DistilGPT2 are related and unusually coordinate-compatible."
            ),
        },
        "runtime": {
            "device": str(device),
            "gpu": (
                torch.cuda.get_device_name(device)
                if device.type == "cuda"
                else None
            ),
            "torch": torch.__version__,
            "elapsed_seconds": elapsed,
            "peak_gpu_memory_mb": (
                torch.cuda.max_memory_allocated(device) / (1024**2)
                if device.type == "cuda"
                else 0.0
            ),
        },
        "models": {
            "a": {"id": args.model_a, "parameters": model_a_parameters},
            "b": {"id": args.model_b, "parameters": model_b_parameters},
            "composite_frozen_parameters": model_a_parameters + model_b_parameters,
            "trainable_base_parameters": trainable_base_parameters,
        },
        "injection": {
            "model_a_zero_based_block_index": args.model_a_block_index,
            "model_a_hidden_state_index": args.model_a_block_index + 1,
            "model_b_hidden_state_index": args.model_b_hidden_index,
            "model_a_blocks_after_injection": (
                len(model_a.transformer.h) - args.model_a_block_index - 1
            ),
            "selected_strategy": selected_mid_key,
            "alpha_model_a": selected_alpha,
            "alpha_model_b": 1.0 - selected_alpha,
        },
        "inputs": {
            "sampling": (
                "stratified evenly spaced blocks across every listed file"
            ),
            "calibration_sha256": sha256_text(calibration_text),
            "evaluation_sha256": sha256_text(evaluation_text),
            "calibration_documents": calibration_sampling,
            "evaluation_documents": evaluation_sampling,
            "sequence_length": args.sequence_length,
            "calibration_blocks": int(calibration_blocks.shape[0]),
            "evaluation_blocks": int(evaluation_blocks.shape[0]),
        },
        "bridge": {
            "type": "centered-orthogonal-procrustes",
            "source": args.model_b,
            "target": args.model_a,
            "hidden_width": int(hidden_a_mid.shape[-1]),
            "paired_calibration_vectors": int(hidden_a_mid.shape[0]),
            "parameters": bridge.parameter_count,
        },
        "selection": {
            "rule": "lowest calibration NLL; evaluation was not consulted",
            "mid_fusion_key": selected_mid_key,
            "native_logit_key": selected_native_logit_key,
            "native_logit_alpha_model_a": native_logit_alpha,
            "final_linear_key": selected_final_linear_key,
            "final_linear_alpha_model_a": final_linear_alpha,
        },
        "calibration": {
            "losses": serialize_losses(calibration_losses),
            "diagnostics": calibration_diagnostics,
        },
        "evaluation": {
            "losses": serialize_losses(evaluation_losses),
            "diagnostics": evaluation_diagnostics,
            "paired_block_comparisons": comparisons,
            "multiple_comparison_rule": (
                "Three confirmatory one-sided sign-flip tests use a "
                "Bonferroni threshold of 0.05 / 3."
            ),
        },
        "gradient_influence": gradient_probe,
        "outcome": {
            "nonlinear_intermediate_fusion_mechanism_demonstrated": (
                trainable_base_parameters == 0
                and both_models_contributed
                and nonlinear_output_changed
                and gradients_reach_both
            ),
            "correctly_paired_model_b_signal_improves_over_shuffle": (
                causal_pairing_signal
            ),
            "useful_midlayer_stack_on_this_held_out_slice": useful_mid_stack,
            "held_out_advantage_statistically_supported": (
                statistically_supported
            ),
            "best_single_key": best_single_key,
            "best_single_nll": best_single_loss,
            "selected_native_logit_nll": native_logit_loss,
            "selected_final_linear_nll": final_linear_loss,
            "selected_intermediate_fusion_nll": selected_loss,
            "shuffled_model_b_control_nll": shuffled_loss,
        },
    }


def main() -> int:
    args = parse_args()
    result = run(args)
    rendered = json.dumps(result, indent=2, ensure_ascii=False)
    print(rendered)
    if args.json_output is not None:
        args.json_output.parent.mkdir(parents=True, exist_ok=True)
        args.json_output.write_text(rendered + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
