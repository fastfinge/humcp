using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Humcp.Core;

namespace Humcp.Core.Tests;

/// <summary>
/// The pinned download (ROADMAP §11.0, Phase 9): every file checked against its hash before it has
/// its own name, a stopped download carried on rather than started again, and a mismatch deleted.
/// Against an in-memory server — the real one is 2.8 GB away.
/// </summary>
public sealed class ModelDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "humcp-models-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _weights = Bytes(300_000, seed: 1);
    private readonly byte[] _config = Bytes(1_000, seed: 2);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Every_file_arrives_checked_and_the_model_is_ready()
    {
        var (model, server) = Serve();
        var store = new ModelStore(_root);
        var reports = new List<ModelDownloadProgress>();

        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, new Recorder(reports), default);

        Assert.Equal(ModelState.Ready, store.StateOf(model));
        Assert.Equal(_weights, File.ReadAllBytes(store.PathOf(model, model.Files[1])));
        Assert.Equal(model.TotalSize, reports[^1].Done);
        Assert.Equal(100, reports[^1].Percent);
        Assert.Empty(Directory.EnumerateFiles(_root, "*" + ModelStore.PartialSuffix, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_file_that_does_not_match_its_hash_is_deleted_not_kept()
    {
        var (model, server) = Serve();
        server.Corrupt = "weights.onnx.data";
        var store = new ModelStore(_root);

        var error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default));

        Assert.Contains("did not match its checksum", error.Message);
        Assert.False(File.Exists(store.PathOf(model, model.Files[1])));
        Assert.False(File.Exists(store.PathOf(model, model.Files[1]) + ModelStore.PartialSuffix));
        Assert.NotEqual(ModelState.Ready, store.StateOf(model));
    }

    [Fact]
    public async Task A_stopped_download_carries_on_from_where_it_stopped()
    {
        var (model, server) = Serve();
        var store = new ModelStore(_root);
        var path = store.PathOf(model, model.Files[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ModelStore.PartialSuffix, _weights[..120_000]);

        Assert.Equal(ModelState.Partial, store.StateOf(model));
        Assert.Equal(120_000, store.BytesOnDisk(model));

        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default);

        Assert.Equal("bytes=120000-", server.Ranges["weights.onnx.data"]);
        Assert.Equal(_weights, File.ReadAllBytes(path));
        Assert.Equal(ModelState.Ready, store.StateOf(model));
    }

    [Fact]
    public async Task A_server_that_ignores_the_range_is_taken_from_the_start()
    {
        var (model, server) = Serve();
        server.IgnoreRange = true;
        var store = new ModelStore(_root);
        var path = store.PathOf(model, model.Files[1]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ModelStore.PartialSuffix, _weights[..50_000]);

        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default);

        Assert.Equal(_weights, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task A_finished_file_is_not_fetched_again()
    {
        var (model, server) = Serve();
        var store = new ModelStore(_root);
        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default);
        server.Requests.Clear();

        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default);

        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task A_refusal_says_so_in_a_sentence()
    {
        var (model, server) = Serve();
        server.Status = HttpStatusCode.NotFound;

        var error = await Assert.ThrowsAsync<ModelDownloadException>(
            () => new ModelDownloader(new HttpClient(server), new ModelStore(_root)).DownloadAsync(model, null, default));

        Assert.Contains("was refused: 404", error.Message);
    }

    [Fact]
    public async Task Removing_a_model_takes_its_folder()
    {
        var (model, server) = Serve();
        var store = new ModelStore(_root);
        await new ModelDownloader(new HttpClient(server), store).DownloadAsync(model, null, default);

        Assert.Null(store.Remove(model));

        Assert.Equal(ModelState.Absent, store.StateOf(model));
        Assert.False(Directory.Exists(store.FolderOf(model)));
    }

    [Fact]
    public void The_cpu_model_is_pinned_to_a_revision_and_every_file_to_a_hash()
    {
        var model = ModelCatalog.Cpu;

        // A commit, never a branch: "main" can move under a hash and every download would then fail.
        Assert.Matches(new Regex("/resolve/[0-9a-f]{40}/"), model.DownloadUrl);
        Assert.All(model.Files, f => Assert.Matches(new Regex("^[0-9a-f]{64}$"), f.Sha256));
        Assert.Equal(model.Files.Count, model.Files.Select(f => f.Path).Distinct().Count());

        // What the spike measured (v2-llm-spike §2): about 2.8 GB on disk.
        Assert.InRange(model.TotalSize, 2_900_000_000, 3_000_000_000);
        Assert.Contains(model.Files, f => f.Path == "genai_config.json");
    }

    [Theory]
    [InlineData(2_924_000_000L, "2.9 gigabytes")]
    [InlineData(740_000_000L, "740 megabytes")]
    [InlineData(5_412L, "5 kilobytes")]
    public void Sizes_are_said_in_words(long bytes, string said)
        => Assert.Equal(said, ModelDownloader.Describe(bytes));

    // ------------------------------------------------------------------ the fake

    private (ModelManifest, FakeHub) Serve()
    {
        var files = new Dictionary<string, byte[]> { ["config.json"] = _config, ["weights.onnx.data"] = _weights };
        var model = new ModelManifest("test-model", "Test model", "MIT", "https://example.invalid/licence",
            "https://example.invalid/", "https://example.invalid/resolve/0123456789012345678901234567890123456789",
            [.. files.Select(f => new ModelFile(f.Key, f.Value.Length, Convert.ToHexStringLower(SHA256.HashData(f.Value))))]);

        return (model, new FakeHub(files));
    }

    private static byte[] Bytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private sealed class Recorder(List<ModelDownloadProgress> into) : IProgress<ModelDownloadProgress>
    {
        public void Report(ModelDownloadProgress value) => into.Add(value);
    }

    /// <summary>Serves files by name, honouring a Range header the way Hugging Face's CDN does.</summary>
    private sealed class FakeHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Dictionary<string, string> Ranges { get; } = [];
        public string? Corrupt { get; set; }
        public bool IgnoreRange { get; set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var name = request.RequestUri!.Segments[^1];
            Requests.Add(name);

            if (Status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(Status));

            var body = files[name].ToArray();
            if (name == Corrupt) body[^1] ^= 0xFF;

            if (request.Headers.Range is { } range && !IgnoreRange)
            {
                Ranges[name] = range.ToString();
                var from = (int)range.Ranges.Single().From!.Value;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body[from..]) });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
        }
    }
}

/// <summary>Which download reports are said aloud (Phase 9): a tenth at a time, never two within ten seconds.</summary>
public class ModelDownloadAnnouncerTests
{
    private DateTime _now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_first_report_is_said_at_once()
    {
        var announcer = new ModelDownloadAnnouncer(() => _now);

        Assert.Equal("Downloading the model: 0 percent, 0.0 of 2.9 gigabytes.",
            announcer.Offer(new ModelDownloadProgress(0, 2_900_000_000)));
    }

    [Fact]
    public void Reports_inside_the_same_tenth_are_quiet()
    {
        var announcer = new ModelDownloadAnnouncer(() => _now);
        announcer.Offer(new ModelDownloadProgress(0, 1000));
        _now += TimeSpan.FromMinutes(1);

        Assert.Null(announcer.Offer(new ModelDownloadProgress(50, 1000)));
        Assert.NotNull(announcer.Offer(new ModelDownloadProgress(100, 1000)));
    }

    [Fact]
    public void Two_tenths_close_together_are_said_once()
    {
        var announcer = new ModelDownloadAnnouncer(() => _now);
        announcer.Offer(new ModelDownloadProgress(0, 1000));

        _now += TimeSpan.FromSeconds(3);
        Assert.Null(announcer.Offer(new ModelDownloadProgress(100, 1000)));

        _now += ModelDownloadAnnouncer.Interval;
        Assert.Contains("20 percent", announcer.Offer(new ModelDownloadProgress(200, 1000)));
    }

    [Fact]
    public void The_end_is_left_to_whoever_says_it_is_checked()
        => Assert.Null(new ModelDownloadAnnouncer(() => _now).Offer(new ModelDownloadProgress(1000, 1000)));
}
