// Throwaway spike for ROADMAP §11.1: does a local model on Windows ML earn each tier?
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntimeGenAI;
using Microsoft.Windows.AI.MachineLearning;

switch (args.FirstOrDefault())
{
    case "probe":
        await Probe();
        break;
    case "bench":
        Bench(Opts(args.Skip(1).ToArray()));
        break;
    default:
        Console.Error.WriteLine("""
            usage: probe
                   bench --model DIR --ep cpu|dml|NvTensorRtRtx|WebGPU [--vendor 0x10DE] [--threads N]
                         [--ctx N] --tag NAME  then either
                         --jobs FILE.json        [{label, prompt, image?, maxNew}]
                         --prefix FILE --queries FILE.json   (prefill once, rewind per query)
            """);
        return 2;
}
return 0;

static Dictionary<string, string> Opts(string[] a)
{
    var d = new Dictionary<string, string>();
    for (int i = 0; i + 1 < a.Length; i += 2) d[a[i].TrimStart('-')] = a[i + 1];
    return d;
}

static void Bench(Dictionary<string, string> o)
{
    var modelDir = o["model"];
    var ep = o.GetValueOrDefault("ep", "cpu");
    var tag = o.GetValueOrDefault("tag", "run");
    var ctx = int.Parse(o.GetValueOrDefault("ctx", "8192"));

    var load = Stopwatch.StartNew();
    using var config = new Config(modelDir);
    // ClearProviders/AppendProvider touch the decoder only; a VLM has three sessions, and a
    // config built for CUDA names CUDA in all three. Overwrite every one.
    var providerOptions = ep == "cpu" ? "[]" : "[{\"" + ep + "\":{" + o.GetValueOrDefault("ep-options", "") + "}}]";
    var so = "\"provider_options\":" + providerOptions
        + (o.TryGetValue("threads", out var threads) ? ",\"intra_op_num_threads\":" + threads : "");
    var session = "{\"session_options\":{" + so + "}}";
    // --vision-ep cpu keeps the vision and embedding graphs on the CPU while the decoder runs on
    // the GPU: the builder makes GPU decoders only, and the published vision graph is a CPU build.
    var visionSession = o.GetValueOrDefault("vision-ep") == "cpu"
        ? "{\"session_options\":{\"provider_options\":[]" + (threads is null ? "" : ",\"intra_op_num_threads\":" + threads) + "}}"
        : session;
    var sessions = new List<string> { "\"decoder\":" + session };
    var raw = JsonNode.Parse(File.ReadAllText(Path.Combine(modelDir, "genai_config.json")))!["model"]!.AsObject();
    foreach (var part in new[] { "embedding", "vision" })
        if (raw.ContainsKey(part)) sessions.Add("\"" + part + "\":" + visionSession);
    // --ep asis leaves the config's providers alone: a vendor build such as AMD's NPU models carries
    // options per session that an overlay would merge into.
    if (ep != "asis") config.Overlay("{\"model\":{" + string.Join(",", sessions) + "}}");
    if (o.TryGetValue("register", out var register)) RegisterWinMl(register);
    if (ep != "cpu" && o.TryGetValue("vendor", out var vendor))
        config.SetDecoderProviderOptionsHardwareVendorId(ep == "asis" ? "dml" : ep, Convert.ToUInt32(vendor, 16));
    if (o.GetValueOrDefault("share") == "false")
        config.Overlay("{\"search\":{\"past_present_share_buffer\":false}}");
    if (ep == "NvTensorRtRtx") RegisterWinMl("NvTensorRTRTXExecutionProvider");
    if (ep == "WebGPU") RegisterWinMl("WebGpuExecutionProvider");
    using var model = new Model(config);
    var loadMs = load.Elapsed.TotalMilliseconds;
    Console.WriteLine($"=== {tag}: loaded in {loadMs:F0} ms");

    // A text-only model (the NPU builds) has no processor; its prompts go in through AppendTokens.
    using var processor = raw.ContainsKey("vision") ? new MultiModalProcessor(model) : null;
    using var tokenizer = new Tokenizer(model);
    var chunk = int.Parse(o.GetValueOrDefault("chunk", "0"));
    // Each family has its own turn markers and its own image placeholder, which the processor expands.
    var type = raw["type"]!.ToString();
    Chat.Family = type.StartsWith("gemma") ? "gemma" : type.StartsWith("phi") ? "phi" : "qwen";
    var meta = new { tag, model = modelDir, ep, threads = threads ?? "default", chunk, loadMs = Math.Round(loadMs) };

    if (o.TryGetValue("jobs", out var jobsFile))
    {
        foreach (var job in JsonNode.Parse(File.ReadAllText(jobsFile))!.AsArray())
        {
            var image = (string?)job!["image"];
            var r = RunOne(model, processor, tokenizer, Path.GetFullPath((string)job["prompt"]!), image, (int?)job["maxNew"] ?? 300, ctx, chunk);
            Record(meta, (string)job["label"]!, r);
        }
    }
    else
    {
        PrefixRun(model, tokenizer, meta, File.ReadAllText(o["prefix"]), o["queries"], ctx, chunk);
    }
}

static Dictionary<string, object?> RunOne(Model model, MultiModalProcessor? processor, Tokenizer tokenizer, string promptFile, string? image, int maxNew, int ctx, int chunk)
{
    var prompt = Chat.UserStart + (image is null ? "" : Chat.Image) + File.ReadAllText(promptFile) + Chat.UserEnd;
    using var stream = processor?.CreateStream() ?? tokenizer.CreateStream();
    var sw = Stopwatch.StartNew();
    using var gp = new GeneratorParams(model);
    gp.SetSearchOption("do_sample", false);
    // The config says 262,144, and a shared KV buffer is allocated for all of it up front.
    gp.SetSearchOption("max_length", ctx);
    using var gen = new Generator(model, gp);
    // Phi-4-multimodal reads images through a LoRA adapter on the language model, which GenAI does
    // not switch on by itself.
    var adapterFile = Environment.GetEnvironmentVariable("SPIKE_VISION_ADAPTER");
    using var adapters = image is null || adapterFile is null ? null : new Adapters(model);
    if (adapters is not null)
    {
        adapters.LoadAdapter(adapterFile!, "vision");
        gen.SetActiveAdapter(adapters, "vision");
    }
    if (processor is null && chunk == 0) chunk = int.MaxValue;
    if (image is null && chunk > 0)
    {
        // Prefill in pieces: one pass over the whole prompt holds attention for all of it at once,
        // which is what took a 10k-token result to 26 GB.
        var ids = tokenizer.Encode(prompt)[0].ToArray();
        for (int i = 0; i < ids.Length; i += chunk)
            gen.AppendTokens(ids.AsSpan(i, Math.Min(chunk, ids.Length - i)));
    }
    else
    {
        using var images = image is null ? null : Images.Load(new[] { image });
        using var inputs = processor.ProcessImages(prompt, images);
        gen.SetInputs(inputs);
    }
    var promptTokens = (int)gen.TokenCount();
    var (text, ttft, produced, total) = Decode(gen, stream, sw, maxNew);
    return new()
    {
        ["prompt"] = Path.GetFileName(promptFile), ["image"] = image is null ? null : Path.GetFileName(image),
        ["promptTokens"] = promptTokens, ["produced"] = produced, ["ttftMs"] = Math.Round(ttft),
        ["totalMs"] = Math.Round(total), ["prefillTps"] = Math.Round(promptTokens / (ttft / 1000), 1),
        ["decodeTps"] = produced > 1 ? Math.Round((produced - 1) / ((total - ttft) / 1000), 1) : 0,
        ["output"] = text,
    };
}

// Tool search with the whole catalogue in front of every question: the catalogue is prefilled
// once, and each query rewinds to the end of it. What the app would do while the list is stable.
static void PrefixRun(Model model, Tokenizer tokenizer, object meta, string prefix, string queriesFile, int ctx, int chunk)
{
    using var gp = new GeneratorParams(model);
    gp.SetSearchOption("do_sample", false);
    gp.SetSearchOption("max_length", ctx);
    using var gen = new Generator(model, gp);
    using var stream = tokenizer.CreateStream();
    var sw = Stopwatch.StartNew();
    var head = tokenizer.Encode(Chat.UserStart + prefix)[0].ToArray();
    var step = chunk > 0 ? chunk : head.Length;
    for (int i = 0; i < head.Length; i += step)
        gen.AppendTokens(head.AsSpan(i, Math.Min(step, head.Length - i)));
    var prefixTokens = gen.TokenCount();
    var prefillMs = sw.Elapsed.TotalMilliseconds;
    Record(meta, "prefix", new() { ["promptTokens"] = (int)prefixTokens, ["ttftMs"] = Math.Round(prefillMs),
        ["prefillTps"] = Math.Round(prefixTokens / (prefillMs / 1000), 1) });

    foreach (var q in JsonNode.Parse(File.ReadAllText(queriesFile))!.AsArray())
    {
        gen.RewindTo(prefixTokens);
        var tail = tokenizer.Encode(
            "\nThe request: \"" + (string)q!["q"]! + "\"\n\nAnswer with the three tool ids that best match, best first, one per line, and nothing else." + Chat.UserEnd);
        var qsw = Stopwatch.StartNew();
        gen.AppendTokens(tail[0]);
        var (text, ttft, produced, total) = Decode(gen, stream, qsw, 60);
        Record(meta, "q:" + (string)q["q"]!, new() { ["promptTokens"] = tail[0].Length, ["produced"] = produced,
            ["ttftMs"] = Math.Round(ttft), ["totalMs"] = Math.Round(total), ["output"] = text,
            ["gold"] = q["gold"]!.ToJsonString() });
    }
}

static (string text, double ttftMs, int produced, double totalMs) Decode(Generator gen, TokenizerStream stream, Stopwatch sw, int maxNew)
{
    var text = new StringBuilder();
    double ttft = 0;
    int produced = 0;
    while (!gen.IsDone() && produced < maxNew)
    {
        gen.GenerateNextToken();
        if (produced == 0) ttft = sw.Elapsed.TotalMilliseconds;
        produced++;
        text.Append(stream.Decode(gen.GetNextTokens()[0]));
    }
    return (text.ToString(), ttft, produced, sw.Elapsed.TotalMilliseconds);
}

static void Record(object meta, string label, Dictionary<string, object?> r)
{
    var node = JsonSerializer.SerializeToNode(meta)!.AsObject();
    node["label"] = label;
    foreach (var (k, v) in r) node[k] = JsonSerializer.SerializeToNode(v);
    Process.GetCurrentProcess().Refresh();
    node["peakWorkingSetMb"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576;
    Directory.CreateDirectory("out");
    File.AppendAllText("out/results.jsonl", node.ToJsonString() + "\n");
    Console.WriteLine($"--- {label}: {r.GetValueOrDefault("promptTokens")} in, TTFT {r.GetValueOrDefault("ttftMs")} ms, "
        + $"{r.GetValueOrDefault("decodeTps")} tok/s, {r.GetValueOrDefault("produced")} out");
    if (r.GetValueOrDefault("output") is string s) Console.WriteLine(s.Trim());
}

static void RegisterWinMl(string name)
{
    var p = ExecutionProviderCatalog.GetDefault().FindAllProviders().Single(p => p.Name == name);
    var sw = Stopwatch.StartNew();
    p.EnsureReadyAsync().AsTask().GetAwaiter().GetResult();
    OrtEnv.Instance().RegisterExecutionProviderLibrary(name, p.LibraryPath);
    Console.WriteLine($"registered {name} in {sw.ElapsedMilliseconds} ms from {p.LibraryPath}");
}

static async Task Probe()
{
    var catalog = ExecutionProviderCatalog.GetDefault();
    var providers = catalog.FindAllProviders();
    foreach (var p in providers)
        Console.WriteLine($"catalog: {p.Name} state={p.ReadyState}");
    var only = Environment.GetEnvironmentVariable("SPIKE_EPS")?.Split(',');
    foreach (var p in providers)
    {
        if (only is not null && !only.Contains(p.Name)) continue;
        Console.WriteLine($"ensuring {p.Name}...");
        try
        {
            var r = await p.EnsureReadyAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1500));
            Console.WriteLine($"  ensure: {r.Status} lib={p.LibraryPath}");
            Console.WriteLine($"  register: {p.TryRegister()}");
        }
        catch (Exception e) { Console.WriteLine($"  failed: {e.Message}"); }
    }
    foreach (var d in OrtEnv.Instance().GetEpDevices())
        Console.WriteLine($"device: {d.EpName} {d.HardwareDevice.Type} vendor={d.EpVendor} hw={d.HardwareDevice.Vendor} id={d.HardwareDevice.DeviceId}");
}

static class Chat
{
    public static string Family = "qwen";
    public static string UserStart => Family switch { "gemma" => "<start_of_turn>user\n", "phi" => "<|user|>", _ => "<|im_start|>user\n" };
    public static string UserEnd => Family switch { "gemma" => "<end_of_turn>\n<start_of_turn>model\n", "phi" => "<|end|><|assistant|>", _ => "<|im_end|>\n<|im_start|>assistant\n" };
    public static string Image => Family switch { "gemma" => "<start_of_image>", "phi" => "<|image_1|>", _ => "<|vision_start|><|image_pad|><|vision_end|>" };
}
