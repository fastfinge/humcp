using System.Text.Json;
using System.Text.Json.Serialization;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// What was typed into a parameter last time, so it can be offered next time (ROADMAP §4,
/// pitfall 3).
/// </summary>
/// <remarks>
/// <para>
/// This exists because the protocol has no answer for it. <c>completion/complete</c> takes
/// <c>ref/prompt</c> and <c>ref/resource</c> and there is no <c>ref/tool</c>, so a server cannot be
/// asked what its own parameter's values look like. Everyone else's client hands the problem to a
/// language model, which is not available here (§2) and would not help a user who has typed the
/// same repository name forty times anyway.
/// </para>
/// <para>
/// <b>It stores what people type into forms, which sooner or later is a password.</b> So it does
/// not store everything: a parameter whose name reads like a credential is never recorded, and
/// neither is anything long enough to be a document or a token. That is a guess, and a guess in
/// this direction costs a drop-down while a guess in the other writes an API key to disk in plain
/// text. Nothing here is encrypted, and it should not be read as though it were: the file is a
/// convenience, and <see cref="Forget"/> empties it.
/// </para>
/// </remarks>
public sealed class MruHistory
{
    /// <summary>How many values are kept for one parameter. Enough to recognise, few enough to arrow through.</summary>
    public const int PerFieldLimit = 10;

    /// <summary>Values longer than this are not recorded: too long to be a name, long enough to be a secret.</summary>
    public const int MaxValueLength = 200;

    private const int Version = 1;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>> _servers = new(StringComparer.Ordinal);
    private readonly string? _path;

    private MruHistory(string? path) => _path = path;

    /// <summary>Overrides where the file lives. Set by the UI suite, which must not write into a real profile.</summary>
    public const string PathVariable = "HUMCP_HISTORY";

    /// <summary>Where the file lives when nobody says otherwise.</summary>
    public static string DefaultPath =>
        Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "humcp", "history.json");

    /// <summary>A store that keeps nothing, for tests and for a user who turns the feature off.</summary>
    public static MruHistory None => new(null);

    /// <summary>
    /// Reads the file if it is there. Never throws: a history that cannot be read is a drop-down
    /// that is empty, which is exactly what it is on the first run anyway.
    /// </summary>
    public static MruHistory Load(string? path = null)
    {
        var file = path ?? DefaultPath;
        var history = new MruHistory(file);

        try
        {
            if (!File.Exists(file)) return history;

            var stored = JsonSerializer.Deserialize<StoredHistory>(File.ReadAllText(file));
            if (stored?.Servers is null) return history;

            foreach (var (server, tools) in stored.Servers)
                foreach (var (tool, fields) in tools)
                    foreach (var (field, values) in fields)
                        foreach (var value in values.Take(PerFieldLimit).Reverse())
                            history.Add(server, tool, field, value);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            history.LastError = TextSanitizer.Clean(ex.Message, TextSanitizer.HelpLength);
        }

        return history;
    }

    /// <summary>Why the last read or write did not happen, for the diagnostics window.</summary>
    public string? LastError { get; private set; }

    /// <summary>Values used for this parameter before, newest first.</summary>
    public IReadOnlyList<string> For(string serverId, string toolName, string fieldPath)
    {
        lock (_gate)
        {
            return _servers.TryGetValue(serverId, out var tools)
                   && tools.TryGetValue(toolName, out var fields)
                   && fields.TryGetValue(fieldPath, out var values)
                ? [.. values]
                : [];
        }
    }

    /// <summary>
    /// Remembers one value, unless it is one of the kinds this deliberately forgets. Returns whether
    /// it was kept, which is what the tests assert on.
    /// </summary>
    public bool Record(string serverId, string toolName, string fieldPath, string? value)
    {
        if (string.IsNullOrWhiteSpace(serverId) || string.IsNullOrWhiteSpace(toolName)) return false;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var trimmed = value.Trim();

        if (trimmed.Length > MaxValueLength) return false;
        if (trimmed.Contains('\n') || trimmed.Contains('\r')) return false;
        if (LooksLikeASecret(fieldPath)) return false;

        lock (_gate) Add(serverId, toolName, fieldPath, trimmed);
        return true;
    }

    /// <summary>Empties the store and the file behind it.</summary>
    public void Forget()
    {
        lock (_gate) _servers.Clear();
        Save();
    }

    /// <summary>
    /// Writes the file. Written whole and moved into place, so an interrupted write leaves the old
    /// history rather than half of a new one.
    /// </summary>
    public void Save()
    {
        if (_path is null) return;

        try
        {
            StoredHistory stored;
            lock (_gate) stored = Snapshot();

            var directory = Path.GetDirectoryName(_path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(stored, Writing));
            File.Move(temporary, _path, overwrite: true);

            LastError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            // A history that cannot be written is a smaller problem than a tool call that fails
            // because of it. It is recorded and then dropped.
            LastError = TextSanitizer.Clean(ex.Message, TextSanitizer.HelpLength);
        }
    }

    /// <summary>
    /// Whether a parameter's name suggests its values should not be written down. Matched on whole
    /// words after the name is broken up, so <c>apiKey</c> and <c>api_key</c> are caught and
    /// <c>keyword</c> and <c>monkey</c> are not.
    /// </summary>
    public static bool LooksLikeASecret(string fieldPath)
    {
        foreach (var word in NameHumanizer.Humanize(fieldPath ?? "").Split(' '))
            if (Secrets.Contains(word.Trim().ToLowerInvariant()))
                return true;

        return false;
    }

    private static readonly HashSet<string> Secrets = new(StringComparer.Ordinal)
    {
        "password", "passwd", "passphrase", "secret", "secrets", "token", "credential", "credentials",
        "key", "apikey", "auth", "authorization", "cookie", "session", "signature", "pin", "otp",
    };

    // ------------------------------------------------------------------ storage

    private void Add(string serverId, string toolName, string fieldPath, string value)
    {
        if (!_servers.TryGetValue(serverId, out var tools))
            _servers[serverId] = tools = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);

        if (!tools.TryGetValue(toolName, out var fields))
            tools[toolName] = fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        if (!fields.TryGetValue(fieldPath, out var values))
            fields[fieldPath] = values = [];

        // Newest first, and used twice is still used once: a drop-down that lists the same
        // repository four times is a drop-down nobody arrows to the bottom of.
        values.RemoveAll(v => string.Equals(v, value, StringComparison.Ordinal));
        values.Insert(0, value);

        if (values.Count > PerFieldLimit) values.RemoveRange(PerFieldLimit, values.Count - PerFieldLimit);
    }

    private StoredHistory Snapshot() => new()
    {
        Version = Version,
        Servers = _servers.ToDictionary(
            server => server.Key,
            server => server.Value.ToDictionary(
                tool => tool.Key,
                tool => tool.Value.ToDictionary(field => field.Key, field => field.Value.ToList())),
            StringComparer.Ordinal),
    };

    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// The file's shape: server, then tool, then parameter path. Nested rather than one dictionary
    /// keyed by a joined string, because a separator that cannot appear in a tool name or a JSON
    /// property name does not exist, and because the file is meant to be readable by whoever finds
    /// it in their profile.
    /// </summary>
    private sealed class StoredHistory
    {
        [JsonPropertyName("version")]
        public int Version { get; set; }

        [JsonPropertyName("servers")]
        public Dictionary<string, Dictionary<string, Dictionary<string, List<string>>>>? Servers { get; set; }
    }
}
