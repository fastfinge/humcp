using System.Diagnostics;
using System.Text;
using Microsoft.ML.OnnxRuntimeGenAI;

namespace Humcp.Inference;

/// <summary>What one generation produced, and how long it took — the spike's measures, kept so the check can say them.</summary>
public sealed record Generation(string Text, int PromptTokens, int Tokens, TimeSpan FirstToken, TimeSpan Total)
{
    /// <summary>Tokens a second after the first, which is the rate a reader waits at.</summary>
    public double TokensPerSecond => Tokens > 1 && Total > FirstToken ? (Tokens - 1) / (Total - FirstToken).TotalSeconds : 0;
}

/// <summary>
/// A downloaded model, loaded and ready to answer: ONNX Runtime GenAI on Windows ML (ROADMAP §11.0),
/// on the processor. Loading takes seconds and a generation takes many, so both run off the caller's
/// thread, and one generation runs at a time.
/// </summary>
/// <remarks>
/// <para>
/// The two things the spike learned that a product has to carry (v2-llm-spike §4): the prompt goes in
/// 512 tokens at a time, because one pass holds attention for all of it and took a long result to
/// 26 GB; and the KV cache is sized to the prompt and the answer, not to the model's 262,144-token
/// window, which GenAI otherwise allocates up front.
/// </para>
/// <para>
/// Images go through GenAI's <c>MultiModalProcessor</c> (<see cref="DescribeImageAsync"/>), made
/// the first time one is asked for: a model that is only ever checked never builds it.
/// </para>
/// </remarks>
public sealed class LocalModel : IDisposable
{
    /// <summary>How much of a prompt goes in at once (v2-llm-spike §4: 512 took a 10k-token prompt from 26 GB to 6.5).</summary>
    public const int PrefillChunk = 512;

    private readonly Model _model;
    private readonly Tokenizer _tokenizer;
    private readonly SemaphoreSlim _one = new(1, 1);
    private bool _disposed;

    private LocalModel(string folder)
    {
        var clock = Stopwatch.StartNew();
        _model = new Model(folder);
        _tokenizer = new Tokenizer(_model);
        LoadTime = clock.Elapsed;
    }

    public TimeSpan LoadTime { get; }

    /// <summary>
    /// Loads the model in <paramref name="folder"/>. Throws <see cref="LocalModelException"/> with a
    /// sentence when it cannot — a damaged file, too little memory, a runtime that would not start.
    /// </summary>
    public static Task<LocalModel> LoadAsync(string folder, CancellationToken cancellation)
        => Task.Run(() =>
        {
            try
            {
                PreloadRuntime();
                return new LocalModel(folder);
            }
            catch (Exception ex) when (ex is OnnxRuntimeGenAIException or DllNotFoundException or BadImageFormatException
                                          or TypeInitializationException or OutOfMemoryException)
            {
                throw new LocalModelException($"The model could not be loaded: {ex.Message}", ex);
            }
        }, cancellation);

    private static readonly Lazy<bool> Preloaded = new(() =>
    {
        // Windows keeps an older onnxruntime.dll of its own in System32. A build without a runtime
        // identifier leaves ours in runtimes\<rid>\native, where .NET finds onnxruntime-genai.dll — but
        // Windows resolves *its* dependency by name, from the application directory and then System32,
        // and the older copy fails to initialise (0x8007045A). Loaded first by full path, ours is the
        // onnxruntime.dll already in the process when GenAI asks. A published build puts both beside
        // the executable and does not need this; a debug build and every test run do (phase9-notes).
        var rid = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "win-arm64",
            _ => "win-x64",
        };

        foreach (var candidate in new[]
                 {
                     Path.Combine(AppContext.BaseDirectory, "onnxruntime.dll"),
                     Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "onnxruntime.dll"),
                 })
        {
            if (File.Exists(candidate) && System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out _)) return true;
        }

        return false;
    });

    private static void PreloadRuntime() => _ = Preloaded.Value;

    /// <summary>
    /// Answers one request, greedily — the same request gets the same answer, which is what a user
    /// checking it twice expects. <paramref name="partial"/> hears the text so far after each token.
    /// </summary>
    public async Task<Generation> GenerateAsync(string request, int maxTokens, IProgress<string>? partial, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Generate(request, maxTokens, partial, cancellation), cancellation).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// The request for an image's description: the spike's (v2-llm-spike §4.1), measured on eight real
    /// images. Kind first, so a listener knows at once whether a chart or a photograph is coming;
    /// only what is visible; and "say so" rather than guess — the failure that matters is a fluent,
    /// specific, wrong answer said to someone who cannot check it.
    /// </summary>
    public const string DescribeRequest =
        "This image was returned by a tool. Describe it for someone who cannot see it. Say first what kind of image it " +
        "is (a photograph, a chart, a screenshot of a web page, an icon), then what it shows, then any text or numbers " +
        "that matter. Describe only what you can see; if something is unclear, say so instead of guessing. Keep it " +
        "under 120 words.";

    /// <summary>A 120-word description with room to finish its sentence.</summary>
    public const int DescribeTokens = 220;

    private MultiModalProcessor? _processor;

    /// <summary>
    /// Describes an image — the bytes as a server sent them, PNG or JPEG or anything else the model's
    /// own decoder reads. One generation at a time, like <see cref="GenerateAsync"/>.
    /// </summary>
    public async Task<Generation> DescribeImageAsync(ReadOnlyMemory<byte> image, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Describe(image.ToArray(), cancellation), cancellation).ConfigureAwait(false);
        }
        catch (OnnxRuntimeGenAIException ex)
        {
            // An image the model's decoder cannot read is the commonest case: a format it does not know.
            throw new LocalModelException($"The model could not describe the image: {ex.Message}", ex);
        }
        finally
        {
            _one.Release();
        }
    }

    private Generation Describe(byte[] image, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        _processor ??= new MultiModalProcessor(_model);

        // The image placeholder Qwen's processor expands into the image's tokens.
        var prompt = $"<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>{DescribeRequest}<|im_end|>\n<|im_start|>assistant\n";

        using var images = Images.Load(image);
        using var inputs = _processor.ProcessImages(prompt, images);

        using var parameters = new GeneratorParams(_model);
        parameters.SetSearchOption("do_sample", false);

        // An image is about a thousand tokens (v2-llm-spike §4); the prompt and answer fit well inside
        // this, and it keeps the KV cache from being sized to the model's whole window.
        parameters.SetSearchOption("max_length", 4096);

        using var generator = new Generator(_model, parameters);
        generator.SetInputs(inputs);
        var promptTokens = (int)generator.TokenCount();

        using var stream = _processor.CreateStream();
        var text = new StringBuilder();
        var first = TimeSpan.Zero;
        var produced = 0;

        while (!generator.IsDone() && produced < DescribeTokens)
        {
            cancellation.ThrowIfCancellationRequested();

            generator.GenerateNextToken();
            if (produced++ == 0) first = clock.Elapsed;

            text.Append(stream.Decode(generator.GetNextTokens()[0]));
        }

        return new Generation(text.ToString().Trim(), promptTokens, produced, first, clock.Elapsed);
    }

    private Generation Generate(string request, int maxTokens, IProgress<string>? partial, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();

        // Qwen's turn markers. The chat template in the model's folder says the same in Jinja, which
        // GenAI 0.18 does not apply to a raw prompt.
        var prompt = $"<|im_start|>user\n{request}<|im_end|>\n<|im_start|>assistant\n";
        var ids = _tokenizer.Encode(prompt)[0].ToArray();

        using var parameters = new GeneratorParams(_model);
        parameters.SetSearchOption("do_sample", false);
        parameters.SetSearchOption("max_length", ids.Length + maxTokens);

        using var generator = new Generator(_model, parameters);
        using var stream = _tokenizer.CreateStream();

        for (var i = 0; i < ids.Length; i += PrefillChunk)
        {
            cancellation.ThrowIfCancellationRequested();
            generator.AppendTokens(ids.AsSpan(i, Math.Min(PrefillChunk, ids.Length - i)));
        }

        var text = new StringBuilder();
        var first = TimeSpan.Zero;
        var produced = 0;

        while (!generator.IsDone() && produced < maxTokens)
        {
            cancellation.ThrowIfCancellationRequested();

            generator.GenerateNextToken();
            if (produced++ == 0) first = clock.Elapsed;

            text.Append(stream.Decode(generator.GetNextTokens()[0]));
            partial?.Report(text.ToString());
        }

        return new Generation(text.ToString().Trim(), ids.Length, produced, first, clock.Elapsed);
    }

    // ------------------------------------------------------------------ read once, ask many times (Phase 11)

    private readonly List<Prefill> _prefills = [];

    /// <summary>
    /// Reads the start of a user turn — a tool catalogue — and keeps it, so that each later request
    /// costs only its own tokens (v2-llm-spike §6: 41 s to read the corpus's catalogue once, half a
    /// second a question after). <paramref name="room"/> is how many tokens a question and its answer
    /// may take beyond it.
    /// </summary>
    public async Task<Prefill> PrefillAsync(string turnStart, int room, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(turnStart);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var ids = _tokenizer.Encode($"<|im_start|>user\n{turnStart}")[0].ToArray();

                var parameters = new GeneratorParams(_model);
                parameters.SetSearchOption("do_sample", false);
                parameters.SetSearchOption("max_length", ids.Length + room);

                var generator = new Generator(_model, parameters);
                for (var i = 0; i < ids.Length; i += PrefillChunk)
                {
                    cancellation.ThrowIfCancellationRequested();
                    generator.AppendTokens(ids.AsSpan(i, Math.Min(PrefillChunk, ids.Length - i)));
                }

                var prefill = new Prefill(parameters, generator, ids.Length, clock.Elapsed);
                lock (_prefills) _prefills.Add(prefill);
                return prefill;
            }, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    /// <summary>
    /// Finishes the user turn begun by <paramref name="prefill"/> with <paramref name="turnEnd"/> and
    /// answers it — then forgets the question, so the next one starts from the catalogue again.
    /// </summary>
    public async Task<Generation> ContinueAsync(Prefill prefill, string turnEnd, int maxTokens, CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(prefill);
        ArgumentNullException.ThrowIfNull(turnEnd);
        ObjectDisposedException.ThrowIf(_disposed || prefill.IsDisposed, this);

        await _one.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            return await Task.Run(() =>
            {
                var clock = Stopwatch.StartNew();
                var generator = prefill.Generator;

                generator.RewindTo((ulong)prefill.Tokens);
                var ids = _tokenizer.Encode($"{turnEnd}<|im_end|>\n<|im_start|>assistant\n")[0].ToArray();
                generator.AppendTokens(ids);

                using var stream = _tokenizer.CreateStream();
                var text = new StringBuilder();
                var first = TimeSpan.Zero;
                var produced = 0;

                while (!generator.IsDone() && produced < maxTokens)
                {
                    cancellation.ThrowIfCancellationRequested();

                    generator.GenerateNextToken();
                    if (produced++ == 0) first = clock.Elapsed;
                    text.Append(stream.Decode(generator.GetNextTokens()[0]));
                }

                return new Generation(text.ToString().Trim(), ids.Length, produced, first, clock.Elapsed);
            }, cancellation).ConfigureAwait(false);
        }
        finally
        {
            _one.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Waits for a generation in flight: disposing the model under it would crash the process.
        _one.Wait();

        lock (_prefills)
        {
            foreach (var prefill in _prefills) prefill.Dispose();
            _prefills.Clear();
        }

        _processor?.Dispose();
        _tokenizer.Dispose();
        _model.Dispose();
        _one.Dispose();
    }
}

/// <summary>A model that would not load or run, with a sentence that says so.</summary>
public sealed class LocalModelException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The start of a turn the model has already read (<see cref="LocalModel.PrefillAsync"/>): a generator
/// holding it, and how many tokens it is, so a question can be appended and then rewound away.
/// Belongs to the model that made it, which disposes it with itself.
/// </summary>
public sealed class Prefill : IDisposable
{
    internal Prefill(GeneratorParams parameters, Generator generator, int tokens, TimeSpan took)
    {
        Parameters = parameters;
        Generator = generator;
        Tokens = tokens;
        Took = took;
    }

    internal GeneratorParams Parameters { get; }

    internal Generator Generator { get; }

    public int Tokens { get; }

    /// <summary>How long the reading took — what the next reading will take, near enough.</summary>
    public TimeSpan Took { get; }

    public bool IsDisposed { get; private set; }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true;

        Generator.Dispose();
        Parameters.Dispose();
    }
}
