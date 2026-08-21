using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace Humcp.Harvester;

/// <summary>
/// Fills <c>tests/fixtures/schemas/</c> by starting real MCP servers and asking them what they can
/// do (ROADMAP §9). Nothing here is part of the app; it exists so the corpus has provenance and can
/// be regenerated rather than curated by hand.
/// </summary>
/// <remarks>
/// Run it with <c>dotnet run --project tools/Humcp.Harvester</c>. Optional arguments are server ids
/// to restrict the run: <c>dotnet run --project tools/Humcp.Harvester -- playwright memory</c>.
/// </remarks>
internal static class Program
{
    /// <summary>Generous: the first run of an npx package downloads it.</summary>
    private static readonly TimeSpan PerServerTimeout = TimeSpan.FromMinutes(4);

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static async Task<int> Main(string[] args)
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            Console.Error.WriteLine("Could not find Humcp.slnx above the working directory.");
            return 1;
        }

        var listPath = Path.Combine(repoRoot, "tools", "Humcp.Harvester", "servers.json");
        var outputRoot = Path.Combine(repoRoot, "tests", "fixtures", "schemas");

        var wanted = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);
        var servers = ReadServerList(listPath)
            .Where(s => wanted.Count == 0 || wanted.Contains(s.Id))
            .ToList();

        if (servers.Count == 0)
        {
            Console.Error.WriteLine($"No servers matched. Known ids are in {listPath}.");
            return 1;
        }

        Console.WriteLine($"Harvesting {servers.Count} server(s) into {outputRoot}");
        Console.WriteLine();

        var results = new List<HarvestResult>();
        foreach (var server in servers)
        {
            var result = await HarvestAsync(server, outputRoot).ConfigureAwait(false);
            results.Add(result);
            Report(result);
        }

        // A partial run must not throw away what the previous full run recorded about servers it
        // did not touch — the manifest is the corpus's index, not this run's log.
        WriteManifest(outputRoot, results, replacingOnly: wanted.Count > 0);
        Summarise(results);

        return results.Any(r => r.Ok) ? 0 : 2;
    }

    // ------------------------------------------------------------------ one server

    private static async Task<HarvestResult> HarvestAsync(ServerEntry server, string outputRoot)
    {
        Console.WriteLine($"── {server.Id} ({server.Package})");

        var directory = Path.Combine(outputRoot, server.Id);
        var stderr = new List<string>();
        using var timeout = new CancellationTokenSource(PerServerTimeout);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Id,
                Command = server.Command,
                Arguments = [.. server.Arguments],
                WorkingDirectory = Scratch(),
                EnvironmentVariables = server.Environment.Count == 0
                    ? null
                    : server.Environment.ToDictionary(e => e.Key, e => (string?)e.Value),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = line => { lock (stderr) stderr.Add(line); },
            });

            await using var client = await McpClient
                .CreateAsync(transport, cancellationToken: timeout.Token).ConfigureAwait(false);

            var tools = await client.ListToolsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);

            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            Directory.CreateDirectory(directory);

            var written = 0;
            foreach (var tool in tools)
            {
                var envelope = new JsonObject
                {
                    ["server"] = server.Id,
                    ["package"] = server.Package,
                    ["name"] = tool.Name,
                    ["title"] = tool.Title,
                    ["description"] = tool.Description,
                    ["annotations"] = ToNode(tool.ProtocolTool.Annotations),
                    ["inputSchema"] = ToNode(tool.JsonSchema),
                    ["outputSchema"] = tool.ProtocolTool.OutputSchema is { } output ? ToNode(output) : null,
                };

                var file = Path.Combine(directory, FileNameFor(tool.Name) + ".json");
                await File.WriteAllTextAsync(file, envelope.ToJsonString(Indented), timeout.Token)
                    .ConfigureAwait(false);
                written++;
            }

            return new HarvestResult(server, true, written, client.NegotiatedProtocolVersion, null,
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            var reason = ex is OperationCanceledException
                ? $"timed out after {PerServerTimeout.TotalMinutes:0} minutes"
                : ex.Message;

            string last;
            lock (stderr) last = stderr.LastOrDefault(l => !string.IsNullOrWhiteSpace(l)) ?? "";
            if (last.Length > 0) reason += $" — server said: {Trim(last, 300)}";

            return new HarvestResult(server, false, 0, null, Trim(reason, 500), stopwatch.Elapsed);
        }
    }

    // ------------------------------------------------------------------ manifest

    private static void WriteManifest(string outputRoot, List<HarvestResult> results, bool replacingOnly)
    {
        var path = Path.Combine(outputRoot, "manifest.json");
        Directory.CreateDirectory(outputRoot);

        var entries = new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase);

        if (replacingOnly && File.Exists(path)
            && JsonNode.Parse(File.ReadAllText(path)) is JsonObject existing
            && existing["servers"] is JsonArray previous)
        {
            foreach (var entry in previous.OfType<JsonObject>())
                if (entry["id"]?.GetValue<string>() is { } id)
                    entries[id] = entry.DeepClone();
        }

        foreach (var result in results) entries[result.Server.Id] = result.ToJson();

        var manifest = new JsonObject
        {
            ["$comment"] = "Generated by tools/Humcp.Harvester. Do not hand-edit; re-run the harvester.",
            ["harvestedUtc"] = DateTime.UtcNow.ToString("O"),
            ["servers"] = new JsonArray([.. entries
                .OrderBy(e => e.Key, StringComparer.Ordinal)
                .Select(e => e.Value)]),
        };

        File.WriteAllText(path, manifest.ToJsonString(Indented));
        Console.WriteLine($"\nManifest: {path}");
    }

    private static void Report(HarvestResult result)
    {
        Console.WriteLine(result.Ok
            ? $"   {result.Tools} tool(s), protocol {result.ProtocolVersion}, {result.Elapsed.TotalSeconds:0.#}s"
            : $"   FAILED: {result.Failure}");
        Console.WriteLine();
    }

    private static void Summarise(List<HarvestResult> results)
    {
        var ok = results.Where(r => r.Ok).ToList();
        Console.WriteLine($"{ok.Count}/{results.Count} servers, {ok.Sum(r => r.Tools)} tools.");

        foreach (var failure in results.Where(r => !r.Ok))
            Console.WriteLine($"  no: {failure.Server.Id} — {failure.Failure}");
    }

    // ------------------------------------------------------------------ plumbing

    private static List<ServerEntry> ReadServerList(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var servers = new List<ServerEntry>();

        foreach (var element in document.RootElement.GetProperty("servers").EnumerateArray())
        {
            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            if (element.TryGetProperty("environment", out var env))
                foreach (var variable in env.EnumerateObject())
                    environment[variable.Name] = variable.Value.GetString() ?? "";

            servers.Add(new ServerEntry(
                element.GetProperty("id").GetString()!,
                element.GetProperty("package").GetString()!,
                element.GetProperty("command").GetString()!,
                [.. element.GetProperty("arguments").EnumerateArray().Select(a => a.GetString() ?? "")],
                environment));
        }

        return servers;
    }

    /// <summary>
    /// Where the harvested servers run. Deliberately not the repository: these are two dozen
    /// third-party programs, one of them takes a directory to expose as an argument, and another
    /// creates its database file wherever it starts. None of that belongs in a working tree.
    /// </summary>
    private static string Scratch()
    {
        var path = Path.Combine(Path.GetTempPath(), "humcp-harvest");
        Directory.CreateDirectory(path);

        return path;
    }

    private static string? FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Humcp.slnx"))) return directory.FullName;
        }

        return null;
    }

    private static JsonNode? ToNode(object? value) => value is null
        ? null
        : JsonSerializer.SerializeToNode(value, value.GetType());

    /// <summary>Tool names are third-party strings and end up as file names. Treat them as hostile.</summary>
    private static string FileNameFor(string toolName)
    {
        var safe = Regex.Replace(toolName, "[^A-Za-z0-9_.-]", "_");
        safe = safe.Trim('.', '_');
        return safe.Length == 0 ? "unnamed" : Trim(safe, 100);
    }

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private sealed record ServerEntry(
        string Id,
        string Package,
        string Command,
        IReadOnlyList<string> Arguments,
        IReadOnlyDictionary<string, string> Environment);

    private sealed record HarvestResult(
        ServerEntry Server,
        bool Ok,
        int Tools,
        string? ProtocolVersion,
        string? Failure,
        TimeSpan Elapsed)
    {
        public JsonObject ToJson() => new()
        {
            ["id"] = Server.Id,
            ["package"] = Server.Package,
            ["command"] = $"{Server.Command} {string.Join(' ', Server.Arguments)}".Trim(),
            ["harvested"] = Ok,
            ["tools"] = Tools,
            ["protocolVersion"] = ProtocolVersion,
            ["failure"] = Failure,
        };
    }
}
