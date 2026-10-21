# Phase 9 — The model on the machine

v2's second phase (ROADMAP §11, "Built in this order"), 2026-10-21. ONNX Runtime GenAI on Windows ML
inside humcp, the pinned model download, and both through the MSI and the zips — the packaging the
spike did not test. Nothing uses the model yet: alt text is Phase 10.

**Status: complete** (2026-10-21). The CPU tier only. Schema 903, Core 224, Inference 2 (one skipped
without a model), UI 86 — all green; the NVDA pass found two defects, both fixed (§5).

## 1. Two decisions taken at the start

- **The NVIDIA tier is deferred, not dropped.** Its model is the spike's own build — Qwen3-VL 4B
  rebuilt unquantized with three patches (v2-llm-spike §5) — so there is nowhere to download it from,
  and it has only ever run on an 8 GB card, where it spilled and was slower than the processor. It
  returns when it has a host and a 12 GB card to be measured on. The tier setting waits with it: a
  setting with one choice is noise.
- **Windows ML is shipped as decided**, against the alternative found during this phase: the plain
  `Microsoft.ML.OnnxRuntimeGenAI` 0.18.0 runs the same CPU model on ONNX Runtime 1.31 under MIT,
  with no Windows-versioned target. The developer's call, made knowing that the Windows ML Runtime
  is under the *Microsoft Software License Terms*, not an open-source licence:
  - redistribution requires end users to agree to terms protecting Microsoft at least as much —
    the installer's licence page now binds them to Microsoft's own, for that component;
  - the distributor indemnifies Microsoft against claims about the application;
  - the runtime "may collect information about you and your use of the software, and send that to
    Microsoft" — said in the user guide's privacy section, the About window and the notices.

## 2. What was built

- **`ModelCatalog.Cpu`** (`src/Humcp.Core/ModelManifest.cs`): `onnx-community/Qwen3-VL-4B-Instruct-ONNX`,
  `cpu-int4-rtn-block-32`, pinned at revision `3b76cbc8`, thirteen files, 2.92 GB. Every SHA-256 was
  checked against the one Hugging Face records for that revision — the LFS files' `X-Linked-ETag`,
  the small files' git blob hashes — and fetched again from the pinned URLs before commit.
- **`ModelStore`**: `%LOCALAPPDATA%\humcp\models\<id>`, local rather than roaming because a roaming
  profile would copy gigabytes to every machine. `HUMCP_MODELS` overrides it for tests. A file has
  its own name only once its hash has matched, so "every file at its pinned size" is the same claim
  as "every file checked", without rehashing 2.9 GB at each start.
- **`ModelDownloader`**: each file as `name.partial`, hashed as it is written; a match is renamed, a
  mismatch deleted. A stopped download resumes with a `Range` request, hashing what is already on disk
  first so the final check still covers every byte; a server that ignores the range is taken from the
  start. It refuses up front when the disk lacks the room, with a sentence that says how much.
- **`ModelDownloadAnnouncer`**: a line every tenth of the way, never two within ten seconds.
- **`Humcp.Inference`**, a new project: `LocalModel` loads a model folder and answers a text request
  off the UI thread, one at a time, greedily, with the spike's two lessons built in — prefill in
  512-token pieces, and a KV cache sized to the prompt and answer rather than 262,144 tokens.
- **File, Local model**: Download (its label carries the state — "Download the rest of the model, 1.0
  of 2.9 gigabytes"), Stop the download, Check the model (loads it and asks one question, saying the
  answer and the timings), Remove the model (asks twice), About the model (licence, source, folder,
  the privacy paragraph, every file's hash).
- **Packaging**: the App and the new project target `net10.0-windows10.0.26100.0` with Windows 11
  (`10.0.22000`) as the floor, since v2 drops Windows 10. Publish puts the natives beside the
  executable for x64 and ARM64. The GenAI package's CUDA provider and import libraries are dropped
  from the publish: Windows ML has no CUDA. The release workflow's installer test now checks for the
  runtime and the Windows ML terms. CI runs the new `Humcp.Inference.Tests`.

| | v1.1.0 | Phase 9 |
|---|---|---|
| `humcp-win-x64.zip` | 60 MB | 86 MB |
| `humcp-x64.msi` | 48 MB | 70 MB |

The growth is ONNX Runtime (22 MB), DirectML (18 MB), GenAI (8.5 MB) and the Windows SDK projection
a versioned target brings (25 MB).

## 3. Findings

### 3.1 Windows' own onnxruntime.dll is found first outside a published build

`Unable to load DLL 'onnxruntime-genai' … A dynamic link library (DLL) initialization routine failed
(0x8007045A)` on the first test run. A build without a runtime identifier keeps the natives in
`runtimes\<rid>\native`; .NET finds `onnxruntime-genai.dll` there, but Windows resolves *its*
dependency by name — the application directory, then System32, where Windows keeps an older
`onnxruntime.dll` of its own. That one fails to initialise. `LocalModel` now loads ours by full path
first. A published build, with everything beside the executable, never had the problem — which is
why it would have shipped working and failed for every developer and every test run.

### 3.2 The real download, measured

From Hugging Face's CDN on the development machine's connection: 2.9 GB in 37 seconds, a line at
22, 50 and 79 percent, then the checked message. Stopped at 1.0 GB and resumed, it carried on from
35 percent and every file matched, including the one that arrived across two runs. The CDN honours
`Range` (206).

### 3.3 The model in the published build

Check the model, from the self-contained x64 publish: loaded in 6–7 seconds, 0.2 seconds to the first
word, 14–15 tokens a second — "A screen reader is a software tool that converts text and other
on-screen content into spoken words for visually impaired users."

## 4. Tests

`ModelDownloadTests` (Core): a fake hub serving bytes and honouring `Range`, for a clean download, a
corrupt file deleted, a resume, a server that ignores the range, a finished file not fetched again, a
refusal in a sentence, removal, and the pinning of the real manifest. `ModelDownloadAnnouncerTests`
for the rate. `Humcp.Inference.Tests` loads the real model when `HUMCP_MODELS` holds one — skipped in
CI, where there is no 2.9 GB — and always checks that a folder with no model is refused in a sentence.

## 5. The NVDA pass

The published build, through the screen reader bridge (silent, *user* stance), with the real download
into a scratch folder. Two defects, both fixed and re-checked by ear:

- **Remove the model could never be confirmed.** The first Remove warns and the second removes — but
  the "asked once" flag was reset when the menu closed, and choosing Remove closes the menu, so the
  second Remove warned again, forever. Now only choosing something else resets it.
- **About the model broke sentences in two.** Its paragraphs were wrapped by hand, and the window
  wrapped them again, so they were read in fragments with some lines heard twice. One line per
  paragraph now.

Heard, and fine: the menu item carrying the size; progress every ten seconds; Stop and the resumed
label; the checked message; Check's answer and timings; Remove's warning and confirmation. An NVDA
update notice opened mid-check and took the foreground, and humcp said "humcp: The model answered."
through the screen reader — the background-speech setting doing what it is for.

### 5.1 Found, and older than v2 — not fixed here

- **NVDA's "read status bar" (NVDA+End) says "No status line found" in humcp.** *Answered by 5.2.* The status bar is a
  UIA `StatusBar`, a direct child of the window, and is the application's only announcement channel —
  yet the one command a user has to hear it again cannot find it. An announcement missed (as the full
  answer was while the update notice had the foreground) is unrecoverable. True since Phase 1.
- **A disabled menu item is skipped by the arrow keys**, so "Check the model" and "Remove the model"
  cannot be discovered until there is a model. WPF's behaviour; the Tool menu's Run has always been
  the same. The About window lists what each item does.

### 5.2 Ctrl+R repeats the last announcement, through prism

The developer's answer to 5.1. Every status line in humcp — the main window's and each form's,
question's, sub-form's and result window's — now writes through one helper (`LiveRegion.Say`), which
remembers the last thing said anywhere; <kbd>Ctrl+R</kbd>, bound in every window by `WindowManager`,
says it again through prism (speech and braille, interrupting), and Help, Repeat last announcement
does the same. Without a screen reader for prism to reach, it goes back into the main window's live
region instead.

It is prism's second use, after speaking from the background, and the same kind: text with no place
on screen the user can be reading it from *at the moment they ask*. A live region is spoken once,
when it changes, and only in the foreground window.

Two things found by ear on the way:

- **Ctrl+Shift+R, the first choice, never reached humcp.** Neither through NVDA nor through Windows'
  own `SendKeys`; a log of every key the window previewed showed Ctrl, Shift and nothing, while
  Ctrl+Shift+Y and Ctrl+R arrived. Another program on the development machine holds it as a
  system-wide hotkey. Any key can collide with something on somebody's machine; Ctrl+R, unclaimed
  here and unused by WPF's text boxes, is the one that was checked.
- **From the menu, the repeat was cut off.** Closing the menu returns the focus, the screen reader
  announces where it landed, and that interrupted the repeat. The menu route waits 600 ms; the key,
  which closes nothing, speaks at once.
