# v2 LLM spike — does a local model earn each tier?

Started 2026-10-20 on `main` after `release/1.x` was cut. The questions are ROADMAP §11.1's: is the
CPU tier worth having, and is the step from a low-end to a high-end GPU worth a tier of its own.
The spike's code is throwaway and lives in `spikes/llm/` (outside the solution, with its own empty
`Directory.Build.props` so none of the repo's build settings reach it).

**Status: measured; the tiers need a decision (§8).** The short version:

- **The 2B model is not good enough** — fluent, specific, wrong numbers (§4.1). **4B is the floor.**
- **The CPU tier is worth having, with 4B**, for alt text and tool search; summaries of long
  results take one to two minutes even here (§4).
- **There is no low-end GPU tier today.** On Windows ML the only route to an AMD or Intel GPU for a
  chat model is DirectML, and GenAI's DirectML path generates garbage for Qwen3-VL (§5.1).
- **The high-end GPU tier is NVIDIA only, and cannot be quantized** — TensorRT-RTX produces
  garbage from every int4 build, so a GPU model is full size: 4B needs a card with about 12 GB.
  On this machine's 8 GB card the 4B is slower than the CPU (§5.2).
- **8B over 4B is a small step** (§4.1), not a tier's worth.
- **Gemma 3 4B runs on DirectML on both GPUs, up to about 1,000 prompt tokens** — DirectML ignores
  its sliding window past that — **but is worse than Qwen3-VL 4B at everything** (§5.3).
- **The NPU installs, but runs no vision model through Windows ML** (§5.4).

## 1. The machine

One laptop spans all three tiers:

| Part | Stands in for |
|---|---|
| AMD Ryzen AI 9 HX 370, 12 cores / 24 threads, 64 GB | CPU tier — capped at 4 intra-op threads to approximate an ordinary laptop, and uncapped for comparison. **This CPU is much faster per core than what most of the audience owns; the 4-thread numbers flatter the CPU tier.** |
| Radeon 890M (integrated, RDNA 3.5) | Low-end GPU |
| GeForce RTX 5070 Laptop, 8 GB | High-end GPU |
| XDNA 2 NPU | Not measured yet — VitisAI EP not obtainable from the catalog here (§3.2) |

## 2. The model family

**Qwen3-VL in 2B, 4B and 8B**, from `onnx-community/Qwen3-VL-*-Instruct-ONNX`, which publishes ONNX
Runtime GenAI builds (`genai_config.json`, separate `vision`, `embedding` and `text` graphs) for
`cpu-int4-rtn-block-32` and `cuda-int4-rtn-block-32`. One family across the tiers means one prompt
format, one chat template and one adapter; the tiers differ only in size.

| Model | CPU build | CUDA build |
|---|---|---|
| Qwen3-VL-2B | 1.4 GB | 2.2 GB |
| Qwen3-VL-4B | 2.8 GB | 3.5 GB |
| Qwen3-VL-8B | 5.0 GB | 6.0 GB |

Other families GenAI 0.18 runs as vision models (from `src/models/model_type.h`): Gemma 3 and 4,
LFM2-VL, Phi-3-vision, Phi-4-multimodal, Qwen2.5-VL, Qwen3.5, Mistral 3. Most of their ONNX uploads
on the Hub are transformers.js layouts, not GenAI ones, so they would have to be converted.

## 3. What Windows ML actually offers for this

### 3.1 Providers on this machine

`ExecutionProviderCatalog.FindAllProviders()` lists three downloadable providers:
`MIGraphXExecutionProvider`, `VitisAIExecutionProvider`, `NvTensorRTRTXExecutionProvider`.
`DmlExecutionProvider` and the CPU provider are built in; DirectML sees both GPUs.

- **MIGraphX (AMD GPU): "not supported for GenAI scenarios today"** — Microsoft's own provider page.
  So the AMD integrated GPU has no vendor path for a chat model.
- **NvTensorRTRTX: obtainable, but `EnsureReadyAsync` took several minutes** the first time (it is a
  Store package download) and appeared to hang under a 3-minute timeout. A user who switches to a
  GPU tier waits for this before the model download even starts.
- **VitisAI (AMD NPU): `EnsureReadyAsync` did not complete** within 3 minutes the first time. Retried
  on 2026-10-21 it installed in seconds — a slow or stalled Store download, not a defect. By then the
  catalog also listed `AMDGPUExecutionProvider` and `RyzenAILightExecutionProvider`; what the NPU can
  run is §5.4.
- **WebGPU: not in the catalog** without the experimental Windows ML package. Microsoft's page says
  it is "not yet recommended for performance-critical production scenarios".
- **Microsoft's own production guidance is vendor EPs for premium hardware and DirectML "for broad
  compatibility"** — despite DirectML's maintenance mode. So DirectML is not dead for us; it is the
  only non-experimental path to an AMD or Intel GPU.

### 3.2 A model build is per provider, and a mismatch fails silently

- The CUDA build will not load anywhere but CUDA: its `genai_config.json` names `cuda` in all three
  sessions, and the WinML GenAI package has no CUDA provider ("CUDA execution provider is not
  enabled in this build"). `Config.ClearProviders()` touches the decoder only; the spike overwrites
  all three sessions through `Config.Overlay`.
- The CPU build under **NvTensorRtRtx loaded, answered correctly, and never touched the GPU** —
  `nvidia-smi` read 0% for the whole run. ONNX Runtime partitioned everything back to the CPU
  without a word. It looked faster than the 4-thread CPU run only because it used every core.
  **A GPU tier that silently runs on the CPU is the worst outcome for a settings choice** — the
  user upgraded and got nothing — so the product must verify where the graph landed.
- The CPU build under **DirectML ran and produced garbage** on both GPUs ("A black and white and the
  yellow leaves of a black and white and the black and the tree branch of the image").

So each GPU tier needs a model built for its provider: TensorRT-RTX for NVIDIA, DirectML (or
WebGPU) for everything else. GenAI's builder (`onnxruntime_genai.models.builder`, `-e dml | webgpu |
NvTensorRtRtx`) builds **only the text decoder** of Qwen3-VL (`Qwen3VLTextModel`); the vision and
embedding graphs come from the published build (§5).

## 4. CPU tier

Two passes, both in `spikes/llm/out/results.jsonl`. The first prefilled each prompt in one piece
and ran the machine out of memory (Claude Code stopped its shell; the benchmark processes finished
regardless). Its time to first word:

| Run | Alt text (1,012 tokens in) | Summary, 2.2k tokens in | Summary, 6.2k in | Summary, 10.4k in | Decode |
|---|---|---|---|---|---|
| 2B, 4 threads | 16–17 s to first word (31 s readings were contaminated by other work) | 31 s | 104 s | 243 s | 7–27 tok/s |
| 2B, all cores | 14 s | 16 s | 59 s | 130 s | 6–15 tok/s |
| 4B, 4 threads | 47–48 s | 73 s | 263 s | 565 s | 2.6–9 tok/s |
| 4B, all cores | 15–23 s | 24 s | 97 s | — | 7–13 tok/s |

**Memory is the finding that matters most.** Peak working set grows with the prompt, not the model:
the 2B model peaked at 7.6 GB on an image, 15.5 GB on a 6k-token result and **26 GB on the 10k-token
accessibility snapshot**; the 4B at 24 GB and **41 GB**. The prompt is prefilled in one pass, so
the attention working set scales with its length. On the 8–16 GB machines the CPU tier is for, a
long result would not finish at all.

**Chunked prefill fixes it.** Feeding the prompt to `AppendTokens` 512 tokens at a time took the
2B model on the 10k-token snapshot from 26 GB to **6.5 GB**, and from 130 s to **61 s** to the
first word, with byte-identical output (2,048-token chunks: 9.9 GB, 65 s). Images go through
`MultiModalProcessor` in one piece, but an image is about 1,000 tokens.

The second pass, chunked at 512 tokens — **these are the CPU tier's numbers**:

| Run | Alt text | Summary, 2.2k in | 3.4k | 5.0k | 6.2k | 10.4k | Peak memory |
|---|---|---|---|---|---|---|---|
| 2B, 4 threads | 16–18 s | 17 s | 27 s | 41 s | 52 s | 98 s | 6.6 GB |
| 2B, all cores | 14 s | 10 s | 16 s | 25 s | 32 s | 61 s | 6.6 GB |
| **4B, 4 threads** | **47–48 s** | **37 s** | **60 s** | **97 s** | **120 s** | **228 s** | 10.3 GB |
| **4B, all cores** | **15–23 s** | **22 s** | **36 s** | **55 s** | **71 s** | **136 s** | 10.3 GB |
| 8B, all cores | 24 s | 37 s | 59 s | 90 s | 113 s | 207 s | 13.5 GB |

Decode is 7–11 tok/s for 4B and 4–8 for 8B, so a 150-word answer adds 20–40 s after the first
word. Peak memory includes a KV cache allocated for the whole 16,384-token context up front (about
4.8 GB of the 4B's 10.3 GB at fp32); a product would size it to the input.

**On an ordinary four-core laptop, then, a 4B alt text is about 50 s and a summary of a long page
two to four minutes.** Alt text and tool search are usable at that speed; a long summary is
something a user starts and comes back to.

### 4.1 Quality, 2B against 4B

Every answer was checked against the image or the source text (greedy decoding, so a run is
repeatable on one machine; the 4-thread and all-core 4B runs differ in places, since thread count
changes the order of floating-point sums).

**Alt text, eight images.**

| Image | 2B | 4B |
|---|---|---|
| Photograph of a cat on a branch | Wrong: "black and white", "facing forward", tail "white with a black tip" (it is striped with a white tip) | Right, including "looking down" and "striped tail" |
| Stacked area chart | Wrong numbers: the blue band "starts at 1.5 billion and rises to 3.5" (it is 0.8 to 1.3) | Right; reads title, axes, legend, no invented numbers |
| Wikipedia *Population growth* | Wrong: "8.3 billion in 2020" (2026), "10.3 billion by 2050" (mid-2080s) | Right, every number |
| humcp on GitHub | Mostly right; lists the branch name `main` as a file | Right; one misread topic ("wrf" for "wpf") |
| Government of Canada weather | Right | Right |
| Wikipedia *Cat* | Garbled time range ("0.0095 to 0.000 years ago") | Time range off by ten ("0.095 Ma"); otherwise right |
| Low-contrast line chart (grey on dark) | Peak "around 1950" (it is about 1988) | Same error, and the wrong axis range — the hard case defeats both |
| 16-pixel MCP logo | "A screenshot of a web page" showing "a book with a bookmark" | "A curved pencil" — neither says it cannot tell |

**Summaries, five results** (claims searched for in the source):

- 4B: nearly every fact traces to the source. Its one consistent error is in the weather, where it
  calls today's high and low "tomorrow's" — raw hourly JSON is the hardest input.
- 2B: the same weather confusion plus a wrong week range; pairs screen readers with the wrong
  systems (ChromeVox as Android's); says humcp is "built with NVDA"; runs to 220 words on a
  150-word limit and repeats itself.

**Tool search**: §6 — the 2B model is worse than keyword search, the 4B nearly perfect.

**So the 2B model is not good enough to put in front of someone who cannot check it.** Its errors
are fluent, specific and plausible — a wrong number said with the same confidence as a right one,
to a listener who has no picture to compare against. The 4B is the smallest model that is.

**8B against 4B** (all cores, same checks). Better in places — it labels today's and tomorrow's
weather correctly, reads the *Cat* range right ("0.0095–0 Ma"), and gets the stacked chart's
blue band right ("around 1 billion") — but it still places the low-contrast chart's peak "around
2020", calls the 16-pixel logo "a squiggle" (though it says the image is too small to be sure, which
the others did not), and adds one unsupported date ("the 1970s"). Tool search 35/35 against 34/35.
**A real step, but a small one, for twice the memory and half the speed.**

## 5. GPU tiers

GenAI's builder makes the decoder; the published vision and embedding graphs are reused
(`spikes/llm/assemble.py`). Three things had to be patched before any GPU build would load, each of
which a product would have to carry:

- **The builder emits `com.microsoft:MRotaryEmbedding`, which Windows ML's ONNX Runtime (1.27)
  does not have.** It already has a decomposed M-RoPE for TensorRT-RTX; `spikes/llm/build-gpu.py`
  gives it to every provider.
- **With tied embeddings the builder quantizes the rotary caches into `GatherBlockQuantized`**,
  which DirectML cannot run and graph capture then refuses. `shared_embeddings=false`.
- **The GPU decoders take fp16 or bf16 embeddings; the published embedding graph emits fp32**, and
  GenAI passes the buffer across without converting. `assemble.py` adds a Cast.

Also: **`Config.Overlay` merges `provider_options` rather than replacing them**, so a provider
option can be added at run time but not removed; the config file has to be written.

### 5.1 DirectML — every GPU, and broken for this model

The patched build places every decoder node on DirectML and loads on both GPUs. Its first words are
right ("This is a photograph of") and everything after degenerates — "a photograph of a photograph
of a photograph…", or a summary that stops after "WCAG 2.". **The same build is correct on the CPU**,
and the published CPU build showed the same pattern on DirectML before any of this, so the fault is
in GenAI's DirectML runtime path for Qwen3-VL, not in the model. The likeliest cause is Qwen3-VL's
three-row position ids not being advanced under graph capture, which GenAI forces on for DirectML.
GenAI also refuses "continuous decoding" on DirectML, so neither chunked prefill nor the catalogue
reuse of §6 is available there.

**So the Radeon 890M — and every AMD and Intel GPU — has no working path for this model on Windows
ML today.** MIGraphX does not support GenAI; WebGPU is not in the catalog. A different model family
might decode correctly on DirectML (Phi-4-multimodal and Gemma 3 do not use M-RoPE); untested.

### 5.2 TensorRT-RTX — NVIDIA, full size only

| Build | Result |
|---|---|
| int4 weights, fp16 activations | Every token "!" — NaN logits |
| int4, bf16 activations (`use_cuda_bf16`) | Same, at block size 32 or 128, with or without an unquantized LM head |
| int4 asymmetric | Fails to load |
| **bf16 or fp16, unquantized** | **Correct** |

Every int4 build is correct on the CPU, so it is TensorRT-RTX's handling of 4-bit QDQ. A GPU model
is therefore full size: **2B is 3.4 GB, 4B 7.7 GB, 8B about 16 GB.** The shared KV buffer does not
match this graph's `present` shape, so KV sharing and CUDA graphs are off — and with them
`RewindTo` and chunked prefill both produce garbage. The first load builds TensorRT engines: 30 s.

2B fp16 on the RTX 5070 (8 GB), vision on the CPU:

| Task | Time to first word | Decode |
|---|---|---|
| Alt text | 7–8 s (the vision graph on the CPU; no faster with it on the GPU) | 45–48 tok/s |
| Summary, 2.2k tokens | 0.2–0.5 s | 40–52 tok/s |
| Summary, 6.2k | 0.8 s | 30 tok/s |
| Summary, 10.4k | **44 s** — the card is full and spills to system memory; later short jobs stay slow | 28 tok/s |
| Tool search, whole catalogue each time (3,980 tokens) | 0.35–0.55 s | 50 tok/s |

**4B fp16 on the same 8 GB card loads and is correct, but spills: 13 s to the first word on 2.2k
tokens and 4.5–7 tok/s — slower than the same model on this CPU.** A 12 GB card would hold it;
there is none here to measure. 8B would need about 20 GB.

### 5.3 Gemma 3 4B — runs on DirectML, but not well enough to use

Gemma 3 does not use M-RoPE, so it tests whether §5.1 is about Qwen3-VL or about DirectML. The build
is AMD's (`amd/Gemma3-4B-it_gpuep_1.0_gpu_128`: GenAI builder, int4 RTN, fp16 activations, no vendor
ops); Google's own weights are gated and not accepted on this account. One patch: its embedding graph
declares `image_features` as rank 3, and GenAI passes rank 2 when there is no image, so every
text-only call failed — the input's shape is cleared and its last-dimension lookup made `-1`.

**On DirectML it is correct up to about 1,000 prompt tokens and wrong after.** Same output as the CPU
for every image description, on both GPUs; a summary prompt is right at 1,182 tokens, slips at 1,210,
and is "Here is the following:" repeated at 1,231. Compared layer by layer against the CPU, DirectML's
`GroupQueryAttention` **ignores `local_window_size`** — Gemma's sliding-window layers attend over the
whole prompt, which matches the CPU with the window removed to 0.6% and the real model not at all.
It ignores an explicit `attention_bias` mask too, so the window cannot be put back from outside the
operator. A DirectML provider bug, in maintenance mode; image descriptions (349 tokens) are unaffected.

Where it works, it is fast: **decode 50–80 tok/s on the Radeon 890M** as well as the RTX 5070, against
7–11 on the CPU. The vision graph stays on the CPU either way, so an image description still takes
17–19 s to its first word (22 s all on the CPU, 39 s on 4 threads).

**And the model is not good enough** (same material, same greedy decoding as Qwen):

| | Qwen3-VL 4B | Gemma 3 4B |
|---|---|---|
| Tool search, top 3 of 35 | 34 | **22** — and invents ids (`filesystem/rename_file`, `github/API-get-pull-request-reviews`) |
| Charts | reads the values | "numbers and labels … unclear", no values from any chart |
| 16-pixel logo | "a curved pencil" / "a paperclip" — wrong | "a microphone" — wrong the same way |
| Repetition | none | four of eight descriptions loop the same sentence to the token limit |
| CPU, 2.5k / 6.3k / 12k-token summary, first word | 22 / 71 / 136 s | 40 / 110 / 228 s |

Gemma resizes every image to a fixed 896×896, so a screenshot's text is unreadable before the model
sees it. **Gemma 3 4B does not replace Qwen3-VL 4B at any tier.** Gemma 4 (E2B/E4B) is newer, but
GenAI's builder makes its text component only ("vision and audio components are not supported"), and
its ONNX uploads are transformers.js layouts — not loadable here without a conversion of our own.

### 5.4 The NPU

The VitisAI provider now installs (§3.1). Two routes, neither of which runs a vision model through
Windows ML today:

- **AMD's NPU build of Gemma 3 4B** (`amd/Gemma-3-4b_rai_1.8.0_npu_16K`) names a `RyzenAI` provider.
  GenAI maps that to `RyzenAILightExecutionProvider` and loads it from `RYZENAI_EP_PATH`; pointed at
  the copy Windows ML installed, it loads and starts AMD's runtime, then stops: **"No supported shapes
  with matching txn_fname_prefix found"** for the first layer norm, whatever the kernel cache path.
  The build carries precompiled NPU kernels for the full Ryzen AI 1.8 SDK runtime, which Windows ML's
  1.8.80 provider package does not match.
- **AMD's documented Windows ML route** (`RyzenAI-SW/WinML/LLM`) is Olive with Quark quantization and
  `VitisGenerateModelLLM`, run with `VitisAIExecutionProvider` — and it and Foundry Local's NPU
  catalog are **text-only models** (Phi-4-mini, Qwen2.5, Mistral 7B). No vision model.

So the NPU could carry summaries and tool search but not alt text, through a model and a conversion
toolchain different from every other tier. Not measured: Foundry Local is not installed here.

## 6. Tool search

The baseline is BM25 over each tool's server, name and description, with 35 requests written for
the spike (in `spikes/llm/material/toolsearch/queries.json` — evaluation data, not corpus fixtures,
so hand-written is right here).

| Method | Right tool in top 3 | Right tool in top 15 |
|---|---|---|
| BM25 | 20 / 35 | 24 / 35 |
| Qwen3-VL-2B, whole catalogue | 18 / 35 (14 first) | — |
| Qwen3-VL-4B, whole catalogue | **34 / 35** (31 first) | — |

**Re-ranking BM25's shortlist cannot work**: 11 of the 35 have no right answer in the top 15 to
re-rank, because the request shares no words with the tool ("how high above sea level is Denver"
→ `maps_elevation`; "draw me a picture" → `generate_image`). A model has to see the whole catalogue.
The catalogue is about 18,700 characters; prefilled once and reused (`Generator.RewindTo`), each
question then costs only its own tokens.

| Model | Catalogue (3,944 tokens), once | Each question, median to first word |
|---|---|---|
| 4B, CPU, 4 threads | 150 s | 1.8 s |
| 4B, CPU, all cores | 41 s | 0.5 s |
| 8B, CPU, all cores | 67 s | 0.8 s |
| 2B, RTX 5070 (no reuse possible, §5.2) | — | 0.35–0.55 s with the whole catalogue |

The catalogue is prefilled again only when the tool list changes. **This is the CPU tier's best
case**: one wait when servers connect, then answers in about a second, right 34 times in 35.

## 7. Material

All under `spikes/llm/material/`, captured on 2026-10-20 from published servers and kept as they
arrived (the corpus rule):

- **Images** (`images/`): Playwright (`@playwright/mcp`, headless) screenshots of a Government of
  Canada weather page, Wikipedia's *Cat* and *Population growth* articles, the humcp GitHub page;
  Playwright pointed directly at two Wikimedia charts (a line chart that renders as low-contrast
  grey on dark, and a stacked area chart) and a photograph (*Cat in tree03.jpg*); and
  `server-everything`'s `get-tiny-image`.
- **Results** (`results/`): Open-Meteo's seven-day hourly forecast (7 KB of JSON); `mcp-server-fetch`
  on the WCAG overview, NVDA's *What's New* and Wikipedia's *Screen reader* (8–20 KB of markdown);
  Playwright's accessibility snapshot of the humcp GitHub page (33 KB).

## 8. What the tiers should be

ROADMAP §11.0 assumed three tiers — CPU only, low-end GPU, high-end GPU — with model size growing
along them. Measured, that shape does not hold:

1. **The model is 4B everywhere.** 2B fails the bar that matters (§4.1); 8B's gain over 4B is
   small and costs twice the memory.
2. **There is no low-end GPU tier** while GenAI's DirectML path cannot run Qwen3-VL (§5.1).
3. **The GPU tier is a speed upgrade for NVIDIA cards with about 12 GB or more**, running the same
   4B unquantized. Same answers, far sooner. Below 12 GB it is slower than the CPU.

So the proposal is **two tiers of one model**: 4B on the CPU (the default; about 10 GB of memory
with a full context, less with one sized to the input), and 4B on an NVIDIA GPU where the card can
hold it. The settings choice is then about speed, not quality, which is also the easier thing to
explain.

Open, before building on it:

- **Whether GenAI's DirectML bug is ours to wait for or to report.** Fixed, it would bring back a
  GPU path for AMD and Intel — and for 8 GB NVIDIA cards, through int4.
- **Whether a model family without M-RoPE does better on DirectML** — Gemma 3 4B does, up to about
  1,000 tokens (§5.3), which is enough for alt text — but it is not good enough at alt text. A vision
  model with neither M-RoPE nor sliding-window attention is the remaining candidate (Phi-4-multimodal).
- **The CPU tier on an ordinary machine.** These numbers are from a 12-core Ryzen AI 9; the
  4-thread rows are a stand-in, not a measurement of a four-core laptop.
- **The NPU** (XDNA 2): installs now, runs no vision model through Windows ML (§5.4). A text-only
  NPU model for summaries and tool search is measurable through Foundry Local.
