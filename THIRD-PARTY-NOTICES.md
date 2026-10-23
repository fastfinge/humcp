# Third-party notices

humcp itself is released under the BSD Zero Clause License (see `LICENSE`). It ships with the
components below, each under its own licence. The full texts are in the `licenses` folder, both in
this repository and beside `Humcp.App.exe` in every release.

| Component | Version | Licence | Source | Text |
|---|---|---|---|---|
| .NET runtime and Windows Desktop (WPF) | 10.0 | MIT | https://github.com/dotnet/runtime, https://github.com/dotnet/wpf | `dotnet-runtime.txt`, `dotnet-wpf.txt`, and their third-party notices |
| ModelContextProtocol.Core, ModelContextProtocol and ModelContextProtocol.Extensions.Tasks (the C# MCP SDK) | 2.2.0 | Apache-2.0, with MIT for earlier contributions | https://github.com/modelcontextprotocol/csharp-sdk | `ModelContextProtocol.txt` |
| Prismatoid (prism screen reader bridge, managed and native) | 0.4.0 | MPL-2.0 | https://github.com/the-byte-bender/Prismatoid | `Prismatoid.txt` |
| JsonSchema.Net, JsonPointer.Net, Json.More.Net | 9.4.0, 7.0.2, 3.0.1 | MIT (source); see below for the binaries | https://github.com/json-everything/json-everything | `json-everything.txt` |
| Humanizer.Core | 3.0.10 | MIT | https://github.com/Humanizr/Humanizer | `Humanizer.txt` |
| Microsoft.Extensions.AI.Abstractions, .Caching.Abstractions, .Configuration.Abstractions, .DependencyInjection.Abstractions, .Diagnostics.Abstractions, .FileProviders.Abstractions, .Hosting.Abstractions, .Logging.Abstractions, .Options, .Primitives | 10.x | MIT | https://github.com/dotnet/extensions, https://github.com/dotnet/runtime | `dotnet-runtime.txt` |
| ONNX Runtime GenAI (Microsoft.ML.OnnxRuntimeGenAI.WinML and .Managed) | 0.18.0 | MIT | https://github.com/microsoft/onnxruntime-genai | `OnnxRuntimeGenAI.txt`, `OnnxRuntimeGenAI-third-party-notices.txt` |
| **Microsoft Windows ML Runtime** (Microsoft.Windows.AI.MachineLearning: ONNX Runtime, DirectML, the Windows ML projection) | 2.4.89 | **Microsoft Software License Terms — not open source** | https://learn.microsoft.com/windows/ai/new-windows-ml/overview | `WindowsML.txt`, `WindowsML-third-party-notices.txt` |

## Windows ML and Microsoft's licence terms

Since v2, humcp ships the Microsoft Windows ML Runtime, which runs the local model. Unlike everything
else here it is not open source: Microsoft licenses it under its own terms (`licenses/WindowsML.txt`),
which permit it to be redistributed with an application on conditions. **Anyone using humcp agrees to
those terms for the Windows ML Runtime**; the installer's licence page says so. Two of them are worth
knowing before you install:

- The runtime "may collect information about you and your use of the software, and send that to
  Microsoft", and you can opt out of many of these scenarios but not all (see https://aka.ms/privacy).
  What you give the model stays on your computer; this is about the runtime itself.
- You may not reverse engineer it, or share or publish it except as part of humcp.

humcp's own code remains under the BSD Zero Clause License. The model it downloads, Qwen3-VL 4B, is
not shipped with humcp; it is fetched on request from Hugging Face and is under the Apache License 2.0
(https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct).

## Prismatoid and the MPL-2.0

Prismatoid is used unmodified, as published on NuGet. Its source code, including the source of the
native `prism.dll` that ships beside humcp, is available at the address above under the Mozilla
Public License 2.0. The MPL applies to Prismatoid's own files only; it places no conditions on
humcp's code.

## The json-everything binaries

The json-everything libraries are MIT-licensed source code. Their maintainers publish the compiled
NuGet packages under an additional *Open Source Maintenance Fee Agreement*, which asks an
organisation with annual gross revenue of US$10,000 or more, using the binaries in
revenue-generating work, to pay a maintenance fee. The agreement states that it is not a licence fee,
that the MIT licence governs where the two conflict, and that it permits redistribution of the
binaries under the MIT licence — which is how humcp ships them. It does not restrict humcp or its
users' rights under the MIT licence. If your organisation would rather not rely on that reading,
build humcp from source with the libraries compiled from their MIT source, which the agreement
explicitly does not cover. See https://github.com/json-everything/json-everything for the
maintainers' own terms.
