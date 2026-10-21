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
/// Text only, for now. Images go through GenAI's <c>MultiModalProcessor</c> in Phase 10, which is
/// where alt text arrives.
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Waits for a generation in flight: disposing the model under it would crash the process.
        _one.Wait();
        _tokenizer.Dispose();
        _model.Dispose();
        _one.Dispose();
    }
}

/// <summary>A model that would not load or run, with a sentence that says so.</summary>
public sealed class LocalModelException(string message, Exception? inner = null) : Exception(message, inner);
