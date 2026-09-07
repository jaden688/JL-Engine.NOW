# JL Hidden-Tensor Fusion

This folder contains two offline, no-API-key experiments using two separately
executed frozen language models:

- GPT-2: 124,439,808 parameters
- DistilGPT2: 81,912,576 parameters
- Combined active frozen parameters: 206,352,384

The experiments are isolated from `dotnet/JLEngine.*`. They do not modify the
solution, runtime, host, database, provider routing, or the existing dirty
worktree.

## Stage 0: final-state fusion

`tensor_fusion_probe.py` captures both models' final hidden tensors and compares
direct fusion, Procrustes-aligned fusion, native-head logit ensembles, and
single-model baselines.

This is a real tensor path, but it is not nonlinear model integration. A final
hidden merge immediately before a linear language-model head is algebraically
equivalent to decoding both states through the same head and averaging those
logits. The probe now measures and reports that equivalence explicitly.

On the stratified held-out slice, the calibration-selected 75/25 direct fusion
scored:

| Path | Held-out NLL |
|---|---:|
| GPT-2 alone | 2.55756 |
| Native-head logit ensemble | 2.50416 |
| Final hidden fusion | 2.48494 |
| Equivalent same-head logit fusion | 2.48461 |
| Shuffled Model-B tensor | 2.62145 |
| Scaled GPT-2-only control | 2.78957 |

The final-state result shows useful paired Model-B signal, but not a unique
hidden-level interaction.

## Stage 1: intermediate nonlinear fusion

`intermediate_fusion_probe.py` performs the stronger test:

1. GPT-2 and DistilGPT2 independently process identical token IDs.
2. GPT-2's state after block 6 and DistilGPT2's state after block 3 are
   captured.
3. Calibration text fits an optional orthogonal coordinate bridge and selects
   the fusion alpha without seeing evaluation results.
4. The selected 75/25 composite tensor replaces GPT-2's block-6 output.
5. Six remaining frozen GPT-2 transformer blocks process the combined state
   before the frozen language-model head.
6. Held-out blocks test the selected path against single models, two output
   ensembles, shuffled Model B, and a scaled-GPT-2-only control.
7. A gradient probe verifies that the final loss depends on both injected
   tensors while no foundation parameter receives a gradient.

Observed Stage-1 result:

| Path | Held-out NLL |
|---|---:|
| GPT-2 alone | 2.95589 |
| Native-head logit ensemble | 2.90480 |
| Final same-head linear ensemble | 2.88888 |
| Intermediate nonlinear fusion | 2.95320 |
| Shuffled Model-B tensor | 3.18803 |
| Scaled GPT-2-only control | 3.05290 |

Mechanism evidence:

- Mean absolute logit difference from the final linear path: 22.29554
- Gradient L2 at GPT-2's injected state: 0.05484
- Gradient L2 at DistilGPT2's injected state: 0.01828
- Foundation parameters receiving gradients: 0
- Correct pairing versus shuffled-B NLL delta: -0.23482
- Exact one-sided paired sign-flip p-value: 1 / 65,536

That proves a genuine nonlinear two-model activation path on this machine. It
does not yet prove a quality win: intermediate fusion was slightly better than
GPT-2 alone on this slice, but the difference was not statistically supported,
and both output ensembles performed better.

## Run

Use the system Python explicitly. The default `python` command currently points
to an unrelated Hermes virtual environment without PyTorch.

```powershell
$python = 'C:\Users\J_lin\AppData\Local\Programs\Python\Python312\python.exe'
$root = 'C:\Users\J_lin\Desktop\jlnow\JL_Engine now\research\tensor_fusion'

& $python "$root\tensor_fusion_probe.py" `
  --json-output "$root\results\stage0.json"

& $python "$root\intermediate_fusion_probe.py" `
  --json-output "$root\results\stage1.json"
```

Both models are cached locally, so the default runs are offline and fail rather
than silently download a missing model. Pass `--allow-download` only for an
intentional Hugging Face download.

## Honest boundary and next experiment

This does not make two parameter counts literally become one larger foundation
model. GPT-2 and DistilGPT2 are related models with the same tokenizer and
hidden width, so this is the favorable mechanical proof, not evidence that
arbitrary proprietary models already share a latent basis.

The next serious experiment is a learned JL-controlled gate/adapter at one or
more intermediate layers, trained while both foundation models remain frozen.
The JL turn snapshot can choose the layer, bridge, and gate once; all model
branches then run against that frozen turn state, and memory/telemetry commit
once after the fused decode. A later heterogeneous-width test can project the
cached Llama 3.2 3B state into GPT-2's residual space.
