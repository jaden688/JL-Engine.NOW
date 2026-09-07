#!/usr/bin/env python3
"""
JL hidden-tensor fusion proof.

This is a deliberately small, offline experiment:

1. Run two frozen causal language models on identical token IDs.
2. Capture the final hidden tensor from each model.
3. Learn an orthogonal Procrustes bridge from model B's basis into model A's.
4. Compare direct and aligned hidden-tensor fusion before model A's frozen
   LM head.
5. Select fusion settings on calibration text only, then measure held-out
   next-token loss against each model and ordinary logit averaging.
6. Shuffle model B's tensor on the exact selected path as a causal control.
7. Bootstrap paired held-out sequence blocks to show uncertainty.

No API keys are used. No foundation-model weights are trained or modified.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import time
from collections import defaultdict
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

import torch
import torch.nn.functional as F
from transformers import AutoModelForCausalLM, AutoTokenizer


DEFAULT_MODEL_A = "gpt2"
DEFAULT_MODEL_B = "distilbert/distilgpt2"
DEFAULT_ALPHAS = (0.0, 0.25, 0.5, 0.75, 1.0)
DEFAULT_BOOTSTRAP_SAMPLES = 10_000


@dataclass(frozen=True)
class TensorBridge:
    source_mean: torch.Tensor
    target_mean: torch.Tensor
    rotation: torch.Tensor
    singular_values: torch.Tensor

    @property
    def parameter_count(self) -> int:
        return (
            self.source_mean.numel()
            + self.target_mean.numel()
            + self.rotation.numel()
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
    parser.add_argument("--evaluation-tokens", type=int, default=1536)
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
    parser.add_argument(
        "--json-output",
        type=Path,
        help="Optional result path. Console JSON is always printed.",
    )
    return parser.parse_args()


def sha256_text(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def load_documents(project_root: Path, relative_paths: Iterable[str]) -> str:
    parts: list[str] = []
    for relative in relative_paths:
        path = project_root / relative
        if not path.is_file():
            raise FileNotFoundError(f"Required experiment input is missing: {path}")
        parts.append(f"\n\n--- {relative} ---\n\n")
        parts.append(path.read_text(encoding="utf-8", errors="replace"))
    return "".join(parts)


def load_document_set(
    project_root: Path,
    relative_paths: Iterable[str],
) -> list[tuple[str, str]]:
    documents: list[tuple[str, str]] = []
    for relative in relative_paths:
        path = project_root / relative
        if not path.is_file():
            raise FileNotFoundError(f"Required experiment input is missing: {path}")
        documents.append(
            (relative, path.read_text(encoding="utf-8", errors="replace"))
        )
    return documents


def render_document_set(documents: Iterable[tuple[str, str]]) -> str:
    return "".join(
        f"\n\n--- {relative} ---\n\n{text}"
        for relative, text in documents
    )


def encode_unbounded(tokenizer: object, text: str) -> list[int]:
    old_limit = tokenizer.model_max_length
    tokenizer.model_max_length = max(len(text), 1_000_000)
    try:
        return tokenizer.encode(text, add_special_tokens=False)
    finally:
        tokenizer.model_max_length = old_limit


def stratified_token_blocks(
    tokenizer: object,
    documents: list[tuple[str, str]],
    maximum_tokens: int,
    sequence_length: int,
) -> tuple[torch.Tensor, list[dict[str, object]]]:
    if not documents:
        raise ValueError("At least one document is required.")
    requested_blocks = maximum_tokens // sequence_length
    if requested_blocks < len(documents):
        raise ValueError(
            "Token budget must fit at least one sequence block per document."
        )

    base_blocks, remainder = divmod(requested_blocks, len(documents))
    sampled_by_document: list[list[torch.Tensor]] = []
    metadata: list[dict[str, object]] = []

    for document_index, (relative, text) in enumerate(documents):
        token_ids = encode_unbounded(tokenizer, text)
        block_count = base_blocks + int(document_index < remainder)
        required = block_count * sequence_length
        if len(token_ids) < required:
            raise RuntimeError(
                f"{relative} has {len(token_ids)} tokens; stratified sampling "
                f"needs at least {required}."
            )

        maximum_start = len(token_ids) - sequence_length
        if block_count == 1:
            starts = [maximum_start // 2]
        else:
            starts = [
                round(index * maximum_start / (block_count - 1))
                for index in range(block_count)
            ]
        sampled_by_document.append(
            [
                torch.tensor(
                    token_ids[start : start + sequence_length],
                    dtype=torch.long,
                )
                for start in starts
            ]
        )
        metadata.append(
            {
                "path": relative,
                "available_tokens": len(token_ids),
                "sampled_blocks": block_count,
                "sampled_start_offsets": starts,
                "sha256": sha256_text(text),
            }
        )

    interleaved = [
        blocks[block_index]
        for block_index in range(max(len(blocks) for blocks in sampled_by_document))
        for blocks in sampled_by_document
        if block_index < len(blocks)
    ]
    return torch.stack(interleaved), metadata


def resolve_device(requested: str) -> torch.device:
    if requested == "auto":
        return torch.device("cuda" if torch.cuda.is_available() else "cpu")
    device = torch.device(requested)
    if device.type == "cuda" and not torch.cuda.is_available():
        raise RuntimeError("CUDA was requested but torch.cuda.is_available() is false.")
    return device


def load_frozen_model(
    name: str,
    device: torch.device,
    allow_download: bool,
) -> tuple[object, torch.nn.Module]:
    tokenizer = AutoTokenizer.from_pretrained(
        name,
        local_files_only=not allow_download,
    )
    dtype = torch.float16 if device.type == "cuda" else torch.float32
    model = AutoModelForCausalLM.from_pretrained(
        name,
        local_files_only=not allow_download,
        dtype=dtype,
        low_cpu_mem_usage=True,
    )
    model.to(device)
    model.eval()
    model.requires_grad_(False)
    return tokenizer, model


def verify_tokenizer_compatibility(tokenizer_a: object, tokenizer_b: object) -> None:
    probes = [
        "Slappy studies a broken engine.",
        "The kernel prepares state before inference.",
        "tensor alignment: 17 -> hot damn",
    ]
    failures: list[str] = []
    for probe in probes:
        ids_a = tokenizer_a.encode(probe, add_special_tokens=False)
        ids_b = tokenizer_b.encode(probe, add_special_tokens=False)
        if ids_a != ids_b:
            failures.append(probe)
    if tokenizer_a.vocab_size != tokenizer_b.vocab_size or failures:
        raise RuntimeError(
            "Stage-0 probe requires identical token IDs and vocabulary. "
            f"vocab A={tokenizer_a.vocab_size}, vocab B={tokenizer_b.vocab_size}, "
            f"failed probes={failures}"
        )


def token_blocks(
    tokenizer: object,
    text: str,
    maximum_tokens: int,
    sequence_length: int,
) -> torch.Tensor:
    token_ids = encode_unbounded(tokenizer, text)

    token_ids = token_ids[:maximum_tokens]
    usable = len(token_ids) - (len(token_ids) % sequence_length)
    if usable < sequence_length:
        raise RuntimeError(
            f"Only {len(token_ids)} tokens were available; need at least "
            f"{sequence_length}."
        )
    return torch.tensor(token_ids[:usable], dtype=torch.long).reshape(
        -1, sequence_length
    )


def iter_batches(blocks: torch.Tensor, batch_size: int) -> Iterable[torch.Tensor]:
    for start in range(0, blocks.shape[0], batch_size):
        yield blocks[start : start + batch_size]


@torch.inference_mode()
def collect_final_hidden(
    model: torch.nn.Module,
    blocks: torch.Tensor,
    batch_size: int,
    device: torch.device,
) -> torch.Tensor:
    rows: list[torch.Tensor] = []
    for cpu_batch in iter_batches(blocks, batch_size):
        batch = cpu_batch.to(device)
        output = model(
            input_ids=batch,
            output_hidden_states=True,
            use_cache=False,
        )
        hidden = output.hidden_states[-1][:, :-1, :]
        rows.append(hidden.detach().float().cpu().reshape(-1, hidden.shape[-1]))
    return torch.cat(rows, dim=0)


def fit_procrustes(source: torch.Tensor, target: torch.Tensor) -> TensorBridge:
    if source.shape != target.shape:
        raise ValueError(
            f"Paired hidden tensors must match; got {source.shape} and {target.shape}."
        )
    source = source.float()
    target = target.float()
    source_mean = source.mean(dim=0)
    target_mean = target.mean(dim=0)
    source_centered = source - source_mean
    target_centered = target - target_mean
    cross_covariance = source_centered.T @ target_centered
    left, singular_values, right_h = torch.linalg.svd(
        cross_covariance,
        full_matrices=False,
    )
    rotation = left @ right_h
    return TensorBridge(
        source_mean=source_mean,
        target_mean=target_mean,
        rotation=rotation,
        singular_values=singular_values,
    )


def map_hidden(hidden: torch.Tensor, bridge: TensorBridge) -> torch.Tensor:
    source_mean = bridge.source_mean.to(hidden.device)
    target_mean = bridge.target_mean.to(hidden.device)
    rotation = bridge.rotation.to(hidden.device)
    return (hidden.float() - source_mean) @ rotation + target_mean


def mean_cosine(left: torch.Tensor, right: torch.Tensor) -> float:
    return float(F.cosine_similarity(left.float(), right.float(), dim=-1).mean())


def perplexity(loss: float) -> float:
    return float(math.exp(min(loss, 20.0)))


def cross_entropy_values(
    logits: torch.Tensor,
    labels: torch.Tensor,
) -> torch.Tensor:
    return F.cross_entropy(
        logits.reshape(-1, logits.shape[-1]).float(),
        labels.reshape(-1),
        reduction="none",
    ).reshape_as(labels)


def record_score(
    totals: defaultdict[str, float],
    block_losses: defaultdict[str, list[float]],
    name: str,
    logits: torch.Tensor,
    labels: torch.Tensor,
    collect_block_losses: bool,
) -> None:
    values = cross_entropy_values(logits, labels)
    totals[name] += float(values.sum().item())
    if collect_block_losses:
        block_losses[name].extend(
            float(value)
            for value in values.mean(dim=1).detach().cpu()
        )


def alpha_from_key(key: str) -> float:
    return float(key.rsplit("_", 1)[-1])


def hidden_partner(
    key: str,
    hidden_b: torch.Tensor,
    mapped_b: torch.Tensor,
) -> torch.Tensor:
    if key.startswith("raw_hidden_alpha_"):
        return hidden_b
    if key.startswith("aligned_hidden_alpha_"):
        return mapped_b
    raise ValueError(f"Unknown hidden-fusion strategy: {key}")


def fuse_hidden(
    key: str,
    hidden_a: torch.Tensor,
    hidden_b: torch.Tensor,
    mapped_b: torch.Tensor,
) -> torch.Tensor:
    alpha = alpha_from_key(key)
    partner = hidden_partner(key, hidden_b, mapped_b)
    return alpha * hidden_a + (1.0 - alpha) * partner


def shuffled_like(hidden: torch.Tensor) -> torch.Tensor:
    flat = hidden.reshape(-1, hidden.shape[-1])
    generator = torch.Generator(device="cpu")
    generator.manual_seed(688 + flat.shape[0])
    permutation = torch.randperm(
        flat.shape[0],
        generator=generator,
        device="cpu",
    ).to(flat.device)
    return flat.index_select(0, permutation).reshape_as(hidden)


def paired_block_bootstrap(
    candidate: torch.Tensor,
    baseline: torch.Tensor,
    samples: int,
    seed: int,
) -> dict[str, object]:
    if candidate.shape != baseline.shape:
        raise ValueError(
            "Paired bootstrap inputs must have identical shapes; "
            f"got {candidate.shape} and {baseline.shape}."
        )
    if candidate.ndim != 1 or candidate.numel() < 2:
        raise ValueError("Paired bootstrap requires at least two sequence blocks.")

    delta = candidate.double() - baseline.double()
    generator = torch.Generator(device="cpu")
    generator.manual_seed(seed)
    indices = torch.randint(
        low=0,
        high=delta.numel(),
        size=(samples, delta.numel()),
        generator=generator,
    )
    bootstrap_means = delta[indices].mean(dim=1)
    interval = torch.quantile(
        bootstrap_means,
        torch.tensor([0.025, 0.975], dtype=torch.float64),
    )
    return {
        "candidate_minus_baseline_mean_nll": float(delta.mean()),
        "ci95_low": float(interval[0]),
        "ci95_high": float(interval[1]),
        "probability_candidate_is_better": float(
            (bootstrap_means < 0.0).double().mean()
        ),
        "paired_blocks": int(delta.numel()),
        "bootstrap_samples": samples,
    }


@torch.inference_mode()
def score_models(
    model_a: torch.nn.Module,
    model_b: torch.nn.Module,
    blocks: torch.Tensor,
    bridge: TensorBridge,
    alphas: tuple[float, ...],
    batch_size: int,
    device: torch.device,
    selected_hidden_key: str | None = None,
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
    maximum_final_linear_equivalence_error = 0.0

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
        hidden_a = output_a.hidden_states[-1][:, :-1, :].float()
        hidden_b = output_b.hidden_states[-1][:, :-1, :].float()
        mapped_b = map_hidden(hidden_b, bridge_device)
        same_head_b_logits = model_a.lm_head(
            hidden_b.to(model_a.lm_head.weight.dtype)
        ).float()

        count = labels.numel()
        token_count += count
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
        record_score(
            totals,
            block_losses,
            "model_b_through_model_a_head",
            same_head_b_logits,
            labels,
            collect_block_losses,
        )

        for alpha in alphas:
            suffix = f"{alpha:.2f}"
            logit_fusion = alpha * logits_a + (1.0 - alpha) * logits_b
            record_score(
                totals,
                block_losses,
                f"logit_alpha_{suffix}",
                logit_fusion,
                labels,
                collect_block_losses,
            )

            raw_hidden = alpha * hidden_a + (1.0 - alpha) * hidden_b
            raw_logits = model_a.lm_head(
                raw_hidden.to(model_a.lm_head.weight.dtype)
            ).float()
            record_score(
                totals,
                block_losses,
                f"raw_hidden_alpha_{suffix}",
                raw_logits,
                labels,
                collect_block_losses,
            )
            same_head_logit_fusion = (
                alpha * logits_a + (1.0 - alpha) * same_head_b_logits
            )
            record_score(
                totals,
                block_losses,
                f"same_head_logit_alpha_{suffix}",
                same_head_logit_fusion,
                labels,
                collect_block_losses,
            )
            maximum_final_linear_equivalence_error = max(
                maximum_final_linear_equivalence_error,
                float((raw_logits - same_head_logit_fusion).abs().max()),
            )

            scaled_model_a_logits = model_a.lm_head(
                (alpha * hidden_a).to(model_a.lm_head.weight.dtype)
            ).float()
            record_score(
                totals,
                block_losses,
                f"scaled_model_a_alpha_{suffix}",
                scaled_model_a_logits,
                labels,
                collect_block_losses,
            )

            fused_hidden = alpha * hidden_a + (1.0 - alpha) * mapped_b
            fused_logits = model_a.lm_head(
                fused_hidden.to(model_a.lm_head.weight.dtype)
            ).float()
            record_score(
                totals,
                block_losses,
                f"aligned_hidden_alpha_{suffix}",
                fused_logits,
                labels,
                collect_block_losses,
            )

        if selected_hidden_key is not None:
            selected_alpha = alpha_from_key(selected_hidden_key)
            selected_partner = hidden_partner(
                selected_hidden_key,
                hidden_b,
                mapped_b,
            )
            shuffled_hidden = selected_alpha * hidden_a + (
                1.0 - selected_alpha
            ) * shuffled_like(selected_partner)
            shuffled_logits = model_a.lm_head(
                shuffled_hidden.to(model_a.lm_head.weight.dtype)
            ).float()
            record_score(
                totals,
                block_losses,
                "selected_hidden_shuffled_b_control",
                shuffled_logits,
                labels,
                collect_block_losses,
            )

        diagnostics["raw_cosine"] += mean_cosine(hidden_a, hidden_b)
        diagnostics["aligned_cosine"] += mean_cosine(hidden_a, mapped_b)
        diagnostics["mapped_b_norm"] += float(mapped_b.norm(dim=-1).mean())
        diagnostics["model_a_norm"] += float(hidden_a.norm(dim=-1).mean())
        diagnostics["model_b_norm"] += float(hidden_b.norm(dim=-1).mean())

    losses = {name: total / token_count for name, total in totals.items()}
    averaged_diagnostics = {
        name: total / batch_count for name, total in diagnostics.items()
    }
    averaged_diagnostics["scored_tokens"] = float(token_count)
    averaged_diagnostics["final_hidden_same_head_logit_max_abs_error"] = (
        maximum_final_linear_equivalence_error
    )
    block_loss_tensors = {
        name: torch.tensor(values, dtype=torch.float64)
        for name, values in block_losses.items()
    }
    return losses, averaged_diagnostics, block_loss_tensors


def choose_best(losses: dict[str, float], prefix: str) -> tuple[str, float, float]:
    candidates = {
        key: value for key, value in losses.items() if key.startswith(prefix)
    }
    key = min(candidates, key=candidates.get)
    alpha = alpha_from_key(key)
    return key, alpha, candidates[key]


def choose_best_hidden(losses: dict[str, float]) -> tuple[str, float, float]:
    candidates = {
        key: value
        for key, value in losses.items()
        if key.startswith(("raw_hidden_alpha_", "aligned_hidden_alpha_"))
    }
    key = min(candidates, key=candidates.get)
    return key, alpha_from_key(key), candidates[key]


@torch.inference_mode()
def next_token_demo(
    tokenizer: object,
    model_a: torch.nn.Module,
    model_b: torch.nn.Module,
    bridge: TensorBridge,
    hidden_key: str,
    prompt: str,
    device: torch.device,
) -> dict[str, object]:
    encoded = tokenizer(prompt, return_tensors="pt", add_special_tokens=False)
    input_ids = encoded["input_ids"].to(device)
    output_a = model_a(
        input_ids=input_ids,
        output_hidden_states=True,
        use_cache=False,
    )
    output_b = model_b(
        input_ids=input_ids,
        output_hidden_states=True,
        use_cache=False,
    )
    hidden_a = output_a.hidden_states[-1][:, -1, :].float()
    hidden_b = output_b.hidden_states[-1][:, -1, :].float()
    mapped_b = map_hidden(hidden_b, bridge)
    fused_hidden = fuse_hidden(hidden_key, hidden_a, hidden_b, mapped_b)
    fused_logits = model_a.lm_head(
        fused_hidden.to(model_a.lm_head.weight.dtype)
    ).float()

    def top_tokens(logits: torch.Tensor) -> list[dict[str, object]]:
        probabilities = logits.softmax(dim=-1)
        values, indices = torch.topk(probabilities, k=5, dim=-1)
        return [
            {
                "token_id": int(token_id),
                "token": tokenizer.decode([int(token_id)]),
                "probability": float(probability),
            }
            for token_id, probability in zip(indices[0], values[0], strict=True)
        ]

    return {
        "prompt": prompt,
        "selected_strategy": hidden_key,
        "model_a": top_tokens(output_a.logits[:, -1, :].float()),
        "model_b": top_tokens(output_b.logits[:, -1, :].float()),
        "selected_hidden_fusion": top_tokens(fused_logits),
    }


def serialize_losses(losses: dict[str, float]) -> dict[str, dict[str, float]]:
    return {
        name: {
            "nll": value,
            "perplexity": perplexity(value),
        }
        for name, value in sorted(losses.items())
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
        (
            "data/agents/Slappy_Full.json",
            "data/JLframe_Engine_Framework.json",
            "data/behavior_states.json",
        ),
    )
    evaluation_documents = load_document_set(
        args.project_root,
        (
            "data/agents/SparkByte_Full.json",
            "dotnet/JLEngine.Core/Engine/JLEngineCore.cs",
        ),
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

    hidden_a = collect_final_hidden(
        model_a,
        calibration_blocks,
        args.batch_size,
        device,
    )
    hidden_b = collect_final_hidden(
        model_b,
        calibration_blocks,
        args.batch_size,
        device,
    )
    bridge = fit_procrustes(hidden_b, hidden_a)

    calibration_losses, calibration_diagnostics, _ = score_models(
        model_a,
        model_b,
        calibration_blocks,
        bridge,
        DEFAULT_ALPHAS,
        args.batch_size,
        device,
    )
    hidden_key, hidden_alpha, _ = choose_best_hidden(calibration_losses)
    logit_key, logit_alpha, _ = choose_best(
        calibration_losses,
        "logit_alpha_",
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
        DEFAULT_ALPHAS,
        args.batch_size,
        device,
        selected_hidden_key=hidden_key,
        collect_block_losses=True,
    )

    model_a_parameters = sum(parameter.numel() for parameter in model_a.parameters())
    model_b_parameters = sum(parameter.numel() for parameter in model_b.parameters())
    trainable_base_parameters = sum(
        parameter.numel()
        for model in (model_a, model_b)
        for parameter in model.parameters()
        if parameter.requires_grad
    )

    selected_hidden_loss = evaluation_losses[hidden_key]
    selected_logit_loss = evaluation_losses[logit_key]
    best_single_key = min(
        ("model_a", "model_b"),
        key=evaluation_losses.get,
    )
    best_single_loss = evaluation_losses[best_single_key]
    useful_stack = selected_hidden_loss < min(
        best_single_loss,
        selected_logit_loss,
    )
    shuffled_key = "selected_hidden_shuffled_b_control"
    shuffled_loss = evaluation_losses[shuffled_key]
    selected_same_head_key = (
        hidden_key.replace("raw_hidden_", "same_head_logit_")
        if hidden_key.startswith("raw_hidden_")
        else None
    )
    selected_same_head_loss = (
        evaluation_losses[selected_same_head_key]
        if selected_same_head_key is not None
        else None
    )
    scaled_model_a_key = f"scaled_model_a_alpha_{hidden_alpha:.2f}"
    scaled_model_a_loss = evaluation_losses[scaled_model_a_key]
    both_models_contributed = 0.0 < hidden_alpha < 1.0
    model_b_has_causal_signal = (
        both_models_contributed and shuffled_loss > selected_hidden_loss
    )
    bootstrap = {
        "unit": (
            f"{args.sequence_length}-token sequence blocks "
            f"({args.sequence_length - 1} next-token predictions each)"
        ),
        "selection_note": (
            "Fusion strategy and alpha were selected on calibration text only; "
            "all intervals below use held-out blocks."
        ),
        "hidden_vs_best_single": paired_block_bootstrap(
            evaluation_block_losses[hidden_key],
            evaluation_block_losses[best_single_key],
            args.bootstrap_samples,
            seed=688,
        ),
        "hidden_vs_selected_logit_ensemble": paired_block_bootstrap(
            evaluation_block_losses[hidden_key],
            evaluation_block_losses[logit_key],
            args.bootstrap_samples,
            seed=689,
        ),
        "correct_pairing_vs_shuffled_model_b": paired_block_bootstrap(
            evaluation_block_losses[hidden_key],
            evaluation_block_losses[shuffled_key],
            args.bootstrap_samples,
            seed=690,
        ),
    }
    advantage_supported = useful_stack and all(
        bootstrap[name]["ci95_high"] < 0.0
        for name in (
            "hidden_vs_best_single",
            "hidden_vs_selected_logit_ensemble",
        )
    )
    causal_pairing_supported = (
        bootstrap["correct_pairing_vs_shuffled_model_b"]["ci95_high"] < 0.0
    )

    prompt = "Slappy studies the broken engine, checks the evidence, and decides to"
    demo = next_token_demo(
        tokenizer_a,
        model_a,
        model_b,
        bridge,
        hidden_key,
        prompt,
        device,
    )

    elapsed = time.perf_counter() - started
    result: dict[str, object] = {
        "experiment": "jl-hidden-tensor-fusion-stage-0",
        "claim_boundary": {
            "mechanism": (
                "Real hidden activations from two independently executed frozen "
                "models entered one selected tensor-fusion calculation before "
                "a frozen language-model head."
            ),
            "not_claimed": (
                "This does not merge weights or turn parameter counts into one "
                "larger foundation model. Because Stage 0 fuses at the final "
                "hidden layer before a linear head, it also does not yet prove "
                "nonlinear cross-model reasoning. It tests the physical tensor "
                "path and whether the second model carries useful paired signal."
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
            "a": {
                "id": args.model_a,
                "parameters": model_a_parameters,
            },
            "b": {
                "id": args.model_b,
                "parameters": model_b_parameters,
            },
            "composite_frozen_parameters": model_a_parameters + model_b_parameters,
            "trainable_base_parameters": trainable_base_parameters,
        },
        "inputs": {
            "calibration_sha256": sha256_text(calibration_text),
            "evaluation_sha256": sha256_text(evaluation_text),
            "calibration_blocks": int(calibration_blocks.shape[0]),
            "evaluation_blocks": int(evaluation_blocks.shape[0]),
            "sequence_length": args.sequence_length,
            "sampling": "stratified evenly spaced blocks across every listed file",
            "calibration_documents": calibration_sampling,
            "evaluation_documents": evaluation_sampling,
        },
        "bridge": {
            "type": "centered-orthogonal-procrustes",
            "source": args.model_b,
            "target": args.model_a,
            "hidden_width": int(hidden_a.shape[-1]),
            "paired_calibration_vectors": int(hidden_a.shape[0]),
            "parameters": bridge.parameter_count,
            "largest_singular_value": float(bridge.singular_values.max()),
            "smallest_singular_value": float(bridge.singular_values.min()),
        },
        "selection": {
            "rule": "lowest calibration NLL; held-out evaluation was not consulted",
            "hidden_fusion_key": hidden_key,
            "hidden_alpha_model_a": hidden_alpha,
            "hidden_alpha_model_b": 1.0 - hidden_alpha,
            "logit_fusion_key": logit_key,
            "logit_alpha_model_a": logit_alpha,
        },
        "calibration": {
            "losses": serialize_losses(calibration_losses),
            "diagnostics": calibration_diagnostics,
        },
        "evaluation": {
            "losses": serialize_losses(evaluation_losses),
            "diagnostics": evaluation_diagnostics,
            "paired_block_bootstrap": bootstrap,
        },
        "controls": {
            "correctly_paired_hidden_nll": selected_hidden_loss,
            "shuffled_model_b_hidden_nll": shuffled_loss,
            "scaled_model_a_only_nll": scaled_model_a_loss,
            "both_models_contributed_to_selected_tensor": both_models_contributed,
            "model_b_has_causal_signal": model_b_has_causal_signal,
            "correct_pairing_advantage_ci_excludes_zero": (
                causal_pairing_supported
            ),
            "procrustes_helped_held_out_cosine_alignment": (
                evaluation_diagnostics["aligned_cosine"]
                > evaluation_diagnostics["raw_cosine"]
            ),
            "final_hidden_fusion_is_linear_same_head_ensemble": True,
            "selected_same_head_equivalent_key": selected_same_head_key,
            "selected_same_head_equivalent_nll": selected_same_head_loss,
            "selected_hidden_minus_same_head_nll": (
                selected_hidden_loss - selected_same_head_loss
                if selected_same_head_loss is not None
                else None
            ),
        },
        "outcome": {
            "mechanism_demonstrated": (
                trainable_base_parameters == 0
                and both_models_contributed
                and model_b_has_causal_signal
            ),
            "useful_stack_on_this_held_out_slice": useful_stack,
            "unique_nonlinear_hidden_interaction_demonstrated": False,
            "held_out_advantage_ci_excludes_zero": advantage_supported,
            "best_single_key": best_single_key,
            "best_single_nll": best_single_loss,
            "selected_logit_ensemble_nll": selected_logit_loss,
            "selected_hidden_fusion_nll": selected_hidden_loss,
            "hidden_nll_improvement_over_best_single_percent": (
                100.0
                * (best_single_loss - selected_hidden_loss)
                / best_single_loss
            ),
        },
        "next_token_demo": demo,
    }
    return result


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
