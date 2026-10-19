using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// What a block of configuration text turned out to contain: the servers that could be read, and a
/// sentence for each entry that could not.
/// </summary>
public sealed record ServerImport(IReadOnlyList<ServerDescriptor> Servers, IReadOnlyList<string> Problems)
{
    public static ServerImport Nothing(string problem) => new([], [problem]);
}

/// <summary>
/// <c>servers.json</c>: the list of servers humcp connects to, kept in the shape of the
/// <c>mcpServers</c> block every other MCP client uses, so that an existing configuration can be
/// pasted in and a humcp one pasted out (ROADMAP §8).
/// </summary>
/// <remarks>
/// <para>
/// <b>The file is the user's as much as it is ours.</b> People edit these by hand and copy them
/// between clients, so a save changes only what humcp manages — <c>command</c>, <c>args</c>,
/// <c>env</c>, <c>cwd</c>, <c>url</c>, <c>title</c>, <c>disabled</c> — and leaves every other
/// member of an entry, and every other member of the file, exactly where it was. A file that could
/// not be read is never saved over: the user's own text is worth more than humcp's opinion of it.
/// </para>
/// <para>
/// Two members are humcp's own and harmless to other clients: <c>title</c>, the name the tree says
/// when the key is not a good one to hear, and <c>disabled</c>, which several clients already use.
/// </para>
/// </remarks>
public sealed class ServerConfiguration
{
    /// <summary>Overrides where the file lives. Set by the UI suite, which must not read a real profile.</summary>
    public const string PathVariable = "HUMCP_CONFIG";

    private const string ServersMember = "mcpServers";

    /// <summary>VS Code's spelling of the same block, read on import and when a file already uses it.</summary>
    private const string AlternateServersMember = "servers";

    private static readonly JsonDocumentOptions Reading = new()
    {
        // VS Code's mcp.json allows both, and it is one of the files people will paste from.
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    private readonly string? _path;
    private readonly JsonObject _document;
    private readonly string _member;
    private readonly List<ServerDescriptor> _servers;
    private readonly bool _unreadable;

    private ServerConfiguration(
        string? path, JsonObject document, string member, List<ServerDescriptor> servers,
        IReadOnlyList<string> problems, bool unreadable, string? error)
    {
        _path = path;
        _document = document;
        _member = member;
        _servers = servers;
        _unreadable = unreadable;
        Problems = problems;
        LastError = error;
    }

    /// <summary>Where the file lives when nobody says otherwise.</summary>
    public static string DefaultPath =>
        System.Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "humcp", "servers.json");

    /// <summary>The file this was read from and will be saved to; null for one held in memory only.</summary>
    public string? FilePath => _path;

    public IReadOnlyList<ServerDescriptor> Servers => _servers;

    /// <summary>Entries in the file that could not be read, one sentence each. They are kept, not dropped.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Why the last read or write did not happen, in words fit to be spoken.</summary>
    public string? LastError { get; private set; }

    /// <summary>An empty configuration that is never written anywhere, for tests.</summary>
    public static ServerConfiguration InMemory(params ServerDescriptor[] servers)
        => new(null, new JsonObject { [ServersMember] = new JsonObject() }, ServersMember,
            [.. servers], [], unreadable: false, error: null);

    /// <summary>
    /// Reads the file if it is there. Never throws: a missing file is a first run, and an unreadable
    /// one is an empty list that says why — and that will not be saved over.
    /// </summary>
    public static ServerConfiguration Load(string? path = null)
    {
        var file = path ?? DefaultPath;

        string text;

        try
        {
            if (!File.Exists(file))
                return new(file, new JsonObject { [ServersMember] = new JsonObject() }, ServersMember, [], [],
                    unreadable: false, error: null);

            text = File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return Unreadable(file, $"humcp could not read {Path.GetFileName(file)}: {ex.Message}");
        }

        JsonObject document;

        try
        {
            document = JsonNode.Parse(text, documentOptions: Reading) as JsonObject
                       ?? throw new JsonException("the file does not hold a JSON object.");
        }
        catch (JsonException ex)
        {
            return Unreadable(file, $"{Path.GetFileName(file)} is not valid JSON, so humcp has not changed it. {ex.Message}");
        }

        var member = document.ContainsKey(ServersMember) || !document.ContainsKey(AlternateServersMember)
            ? ServersMember
            : AlternateServersMember;

        if (document[member] is not JsonObject entries)
        {
            if (document.ContainsKey(member))
                return Unreadable(file, $"In {Path.GetFileName(file)}, {member} is not an object, so humcp has not changed it.");

            document[member] = entries = new JsonObject();
        }

        var servers = new List<ServerDescriptor>();
        var problems = new List<string>();

        foreach (var (key, value) in entries)
        {
            if (Read(key, value, out var problem) is { } server) servers.Add(server);
            else problems.Add(problem!);
        }

        return new(file, document, member, servers, problems, unreadable: false, error: null);
    }

    /// <summary>
    /// Reads servers out of text someone pasted: a whole <c>claude_desktop_config.json</c>, VS Code's
    /// <c>mcp.json</c>, the <c>mcpServers</c> object on its own, one named entry with or without the
    /// braces around it, or one bare entry. Whatever every other client's documentation tells people
    /// to copy should work here.
    /// </summary>
    public static ServerImport Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return ServerImport.Nothing("There is nothing to import.");

        JsonObject? root = TryParse(text) ?? TryParse("{" + text.Trim().TrimEnd(',') + "}");

        if (root is null)
            return ServerImport.Nothing("That is not JSON that humcp can read. Paste a block that starts with a brace.");

        JsonObject entries;

        if (root[ServersMember] is JsonObject servers) entries = servers;
        else if (root[AlternateServersMember] is JsonObject alternate) entries = alternate;
        else if (LooksLikeAnEntry(root)) entries = new JsonObject { ["server"] = root.DeepClone() };
        else entries = root;

        var read = new List<ServerDescriptor>();
        var problems = new List<string>();

        foreach (var (key, value) in entries)
        {
            if (Read(key, value, out var problem) is { } server) read.Add(server);
            else problems.Add(problem!);
        }

        if (read.Count == 0 && problems.Count == 0) problems.Add("That block names no servers.");
        return new(read, problems);
    }

    /// <summary>The descriptor filed under <paramref name="id"/>, or null.</summary>
    public ServerDescriptor? Find(string id) => _servers.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// A key not already in use, made from <paramref name="wanted"/>: <c>github</c>, then
    /// <c>github-2</c>, and so on. Used on import, where a pasted block can name a server that is
    /// already configured.
    /// </summary>
    public string UniqueId(string wanted, string? except = null)
    {
        var stem = string.IsNullOrWhiteSpace(wanted) ? "server" : wanted.Trim();
        var candidate = stem;

        for (var n = 2; Taken(candidate) && candidate != except; n++)
            candidate = $"{stem}-{n}";

        return candidate;
    }

    /// <summary>
    /// Whether a key is in use — by a server, or by an entry that could not be read and is still
    /// in the file, which a new server must not land on top of.
    /// </summary>
    public bool Taken(string id)
        => _servers.Any(s => s.Id == id) || (_document[_member] as JsonObject)?.ContainsKey(id) == true;

    /// <summary>
    /// Adds a server, or replaces the one filed under <paramref name="previousId"/> (its own id when
    /// that is null). A replaced server keeps its place in the list and in the file.
    /// </summary>
    public void Put(ServerDescriptor server, string? previousId = null)
    {
        ArgumentNullException.ThrowIfNull(server);

        var oldId = previousId ?? server.Id;
        var index = _servers.FindIndex(s => s.Id == oldId);

        if (index >= 0) _servers[index] = server;
        else _servers.Add(server);

        if (_document[_member] is not JsonObject entries) return;

        var entry = entries[oldId] as JsonObject ?? new JsonObject();

        if (oldId != server.Id)
        {
            entries.Remove(oldId);
            entries.Remove(server.Id);
            entries[server.Id] = Detach(entry);
            entry = (JsonObject)entries[server.Id]!;
        }
        else if (!entries.ContainsKey(server.Id))
        {
            entries[server.Id] = entry;
        }

        Write(entry, server);
    }

    public bool Remove(string id)
    {
        var removed = _servers.RemoveAll(s => s.Id == id) > 0;
        if (_document[_member] is JsonObject entries) entries.Remove(id);
        return removed;
    }

    /// <summary>
    /// Writes the file whole and moves it into place, so an interrupted save leaves the old list
    /// rather than half of a new one. Answers whether it was written.
    /// </summary>
    public bool Save()
    {
        if (_path is null) return true;

        if (_unreadable)
        {
            // LastError already says why; repeating that here keeps it said.
            return false;
        }

        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, _document.ToJsonString(Writing));
            File.Move(temporary, _path, overwrite: true);

            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            LastError = TextSanitizer.Clean($"humcp could not save {Path.GetFileName(_path)}: {ex.Message}",
                TextSanitizer.HelpLength);
            return false;
        }
    }

    /// <summary>One server as an <c>mcpServers</c> block, for Copy and for anyone pasting it elsewhere.</summary>
    public static string ToJson(IEnumerable<ServerDescriptor> servers)
    {
        var entries = new JsonObject();

        foreach (var server in servers)
        {
            var entry = new JsonObject();
            Write(entry, server);
            entries[server.Id] = entry;
        }

        return new JsonObject { [ServersMember] = entries }.ToJsonString(Writing);
    }

    // ------------------------------------------------------------------ reading an entry

    private static ServerDescriptor? Read(string key, JsonNode? value, out string? problem)
    {
        problem = null;
        var name = TextSanitizer.Clean(key, TextSanitizer.NameLength);

        if (string.IsNullOrWhiteSpace(key))
        {
            problem = "An entry has no name.";
            return null;
        }

        if (value is not JsonObject entry)
        {
            problem = $"{name} is not an object with a command in it.";
            return null;
        }

        var command = String(entry["command"]);
        var url = String(entry["url"]) ?? String(entry["serverUrl"]);

        if (string.IsNullOrWhiteSpace(command) && string.IsNullOrWhiteSpace(url))
        {
            problem = $"{name} has neither a command nor an address.";
            return null;
        }

        List<string> arguments = [];

        switch (entry["args"])
        {
            case null:
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue scalar && scalar.GetValueKind() is not (JsonValueKind.Object or JsonValueKind.Array))
                    {
                        arguments.Add(scalar.GetValueKind() == JsonValueKind.String
                            ? scalar.GetValue<string>()
                            : scalar.ToJsonString());
                    }
                    else
                    {
                        problem = $"{name} has an argument that is not text.";
                        return null;
                    }
                }
                break;
            case JsonValue single when single.GetValueKind() == JsonValueKind.String:
                // Not what any client writes, and exactly what someone typing a file by hand does.
                arguments.AddRange(ServerDescriptor.SplitArguments(single.GetValue<string>()));
                break;
            default:
                problem = $"{name} has args that are not a list.";
                return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        if (entry["env"] is JsonObject env)
        {
            foreach (var (variable, setting) in env)
            {
                if (setting is null) continue;
                environment[variable] = setting.GetValueKind() == JsonValueKind.String
                    ? setting.GetValue<string>()
                    : setting.ToJsonString();
            }
        }
        else if (entry["env"] is not null)
        {
            problem = $"{name} has env that is not an object of names and values.";
            return null;
        }

        var title = String(entry["title"]);

        return new ServerDescriptor(
            Id: key,
            DisplayName: TextSanitizer.Clean(string.IsNullOrWhiteSpace(title) ? key : title, TextSanitizer.NameLength),
            Command: command?.Trim() ?? "",
            Arguments: arguments,
            WorkingDirectory: String(entry["cwd"]) is { Length: > 0 } cwd ? cwd : null)
        {
            Environment = environment,
            Url = string.IsNullOrWhiteSpace(command) ? url?.Trim() : null,
            Enabled = !(entry["disabled"] is JsonValue disabled
                        && disabled.GetValueKind() == JsonValueKind.True),
        };
    }

    private static bool LooksLikeAnEntry(JsonObject node)
        => node["command"] is JsonValue || node["url"] is JsonValue;

    private static string? String(JsonNode? node)
        => node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static JsonObject? TryParse(string text)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: Reading) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ writing an entry

    /// <summary>
    /// Sets the members humcp manages and nothing else. A member at its default is removed rather
    /// than written out, so a file humcp saves looks like one a person would have written.
    /// </summary>
    private static void Write(JsonObject entry, ServerDescriptor server)
    {
        if (server.Url is { Length: > 0 } url)
        {
            entry.Remove("command");
            entry.Remove("args");
            entry["url"] = url;
        }
        else
        {
            entry.Remove("url");
            entry["command"] = server.Command;

            if (server.Arguments.Count > 0)
                entry["args"] = new JsonArray([.. server.Arguments.Select(a => (JsonNode?)JsonValue.Create(a))]);
            else
                entry.Remove("args");
        }

        if (server.Environment.Count > 0)
        {
            var env = new JsonObject();
            foreach (var (name, value) in server.Environment) env[name] = value;
            entry["env"] = env;
        }
        else
        {
            entry.Remove("env");
        }

        Set(entry, "cwd", server.WorkingDirectory);
        Set(entry, "title", server.DisplayName == server.Id ? null : server.DisplayName);

        if (server.Enabled) entry.Remove("disabled");
        else entry["disabled"] = true;
    }

    private static void Set(JsonObject entry, string member, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) entry.Remove(member);
        else entry[member] = value;
    }

    /// <summary>A node has one parent, and moving an entry to a new key means giving it a new one.</summary>
    private static JsonObject Detach(JsonObject entry)
        => entry.Parent is null ? entry : (JsonObject)entry.DeepClone();

    private static ServerConfiguration Unreadable(string file, string error)
        => new(file, new JsonObject { [ServersMember] = new JsonObject() }, ServersMember, [], [],
            unreadable: true, error: TextSanitizer.Clean(error, TextSanitizer.HelpLength));
}
