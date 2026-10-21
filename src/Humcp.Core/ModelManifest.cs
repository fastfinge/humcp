namespace Humcp.Core;

/// <summary>One file of a model, pinned: where it is under the model's folder, its exact size, and its SHA-256.</summary>
public sealed record ModelFile(string Path, long Size, string Sha256);

/// <summary>
/// A model humcp can download, pinned to one revision of one upload (ROADMAP §11.0): "a tier is a
/// pinned model, not a model browser". Every file is named with its size and hash, so nothing a
/// server could change after the fact is ever loaded.
/// </summary>
/// <remarks>
/// A model from a hub is a third-party file, like a server's schema. The hashes here are what make
/// it safe to fetch: a file that does not match is deleted before anything reads it.
/// </remarks>
public sealed record ModelManifest(
    string Id,
    string Name,
    string Licence,
    string LicenceUrl,
    string SourceUrl,
    string DownloadUrl,
    IReadOnlyList<ModelFile> Files)
{
    public long TotalSize => Files.Sum(f => f.Size);

    /// <summary>Where a file comes from: the pinned revision, never a branch that can move.</summary>
    public Uri UrlOf(ModelFile file) => new($"{DownloadUrl.TrimEnd('/')}/{file.Path}");
}

/// <summary>The models humcp knows. One, until the NVIDIA tier has somewhere to come from (§11, Phase 9).</summary>
public static class ModelCatalog
{
    /// <summary>
    /// The CPU tier: Qwen3-VL 4B, int4, as onnx-community publishes it for ONNX Runtime GenAI.
    /// Pinned at the revision the v2 spike measured (v2-llm-spike §2, §4), and every hash checked
    /// against the one Hugging Face records for that revision.
    /// </summary>
    public static ModelManifest Cpu { get; } = new(
        Id: "qwen3-vl-4b-cpu-int4",
        Name: "Qwen3-VL 4B, for the processor",
        Licence: "Apache License 2.0",
        LicenceUrl: "https://huggingface.co/Qwen/Qwen3-VL-4B-Instruct/blob/main/LICENSE",
        SourceUrl: "https://huggingface.co/onnx-community/Qwen3-VL-4B-Instruct-ONNX",
        DownloadUrl: "https://huggingface.co/onnx-community/Qwen3-VL-4B-Instruct-ONNX/resolve/"
                     + "3b76cbc8c52439369979d4495ce803abf9d3681a/onnxruntime/cpu_and_mobile/cpu-int4-rtn-block-32",
        Files:
        [
            new("chat_template.jinja", 5412, "24a1eb036569714fc3efe7908495159c19ac5138f652c9e524475e40ce87d716"),
            new("config.json", 1594, "515c5fc204a21eb086e8ad2f60248edfc537a010240d8cb5c6b74d321514350c"),
            new("embedding.onnx", 2337, "0e763665d3c872a8f91f9c5e2c084c9f21d580818d986be6a5813bf9ebab62f2"),
            new("embedding.onnx.data", 206667776, "66345e6725ffdd5ba51f693b95ac6d28d4a5e2e70203f970000a21588c5a9d9f"),
            new("genai_config.json", 2798, "7d88f9fd77b1a5bd2215ddfe9c841e02bbea808ff5315d530ed27cf27729ad3e"),
            new("generation_config.json", 255, "a91ac8552b94a54736d05df0f9c4df38ea12061b830ceba093327a6577d1528d"),
            new("processor_config.json", 1604, "bfd0b54627a192f66186cbe537c04f782b55631135d2e527bad489165a9ce3ca"),
            new("text.onnx", 1349047, "5782c5f514484cf20b92f455f3f36a46d652e062abb1311db355f8a4989c0e36"),
            new("text.onnx.data", 2518253568, "f73addc1a6d9952b39e172abf5f9b3fe68504e870f1561c45445d3f00cb02c9f"),
            new("tokenizer.json", 11422649, "83be2230e69d031c94eb1d76bbc99fefac97eef09ab156791a65a66f048ecb83"),
            new("tokenizer_config.json", 694, "4fc29f241a5d042a2508e314991113d4b5ad144558fbb631f4a60899ad8ca1d5"),
            new("vision.onnx", 2948457, "bb8f7631391fadc7e23ce8536433c128d8a2ad208c8e007e8870760400680758"),
            new("vision.onnx.data", 183828480, "486403e1e62348cdcbdc18319027be976aa1e1def09595d36c7734a2c6911ea9"),
        ]);
}
