using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Schema;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Humcp.Core;

public enum ServerConnectionState
{
    NotConnected,
    Connecting,
    Connected,
    Failed,
}

/// <summary>
/// A server as the UI needs to see it at one instant. The tree is rebuilt from these, so nothing
/// in the view ever holds a half-connected object.
/// </summary>
public sealed record ServerSnapshot(
    string DisplayName,
    ServerConnectionState State,
    IReadOnlyList<ToolInfo> Tools,
    string? NegotiatedProtocolVersion = null,
    string? FailureReason = null);

/// <summary>
/// One live connection to one MCP server, and the only place in the app that knows the protocol
/// exists. Everything it hands out is a neutral model (ROADMAP §6.1).
/// </summary>
/// <remarks>
/// The protocol is never hand-rolled here (§4, pitfall 8): the SDK negotiates the revision, and the
/// result is published as <see cref="ServerSnapshot.NegotiatedProtocolVersion"/> so a
/// misbehaving server is diagnosable from the tree.
/// </remarks>
public sealed class ServerConnection : IAsyncDisposable
{
    private const int DiagnosticLineLimit = 200;

    /// <summary>An empty argument object. Deliberately not <c>null</c> — see <see cref="CallAsync"/>.</summary>
    private static readonly IReadOnlyDictionary<string, object?> NoArguments =
        new Dictionary<string, object?>();

    private readonly ConcurrentQueue<string> _diagnostics = new();
    private McpClient? _client;
    private IReadOnlyList<ToolInfo> _tools = [];

    public ServerConnection(ServerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
    }

    public ServerDescriptor Descriptor { get; }

    public ServerConnectionState State { get; private set; } = ServerConnectionState.NotConnected;

    public string? FailureReason { get; private set; }

    public string? NegotiatedProtocolVersion { get; private set; }

    /// <summary>The server's own title, once it has said one. Untrusted, so already sanitised.</summary>
    public string? ServerTitle { get; private set; }

    public IReadOnlyList<ToolInfo> Tools => _tools;

    public ServerSnapshot Snapshot() => new(
        Descriptor.DisplayName, State, _tools, NegotiatedProtocolVersion, FailureReason);

    /// <summary>
    /// Standard error from the child process, newest last. §8 shows a server's failure on its tree
    /// node; this is what makes that message say something useful.
    /// </summary>
    public IReadOnlyList<string> Diagnostics => _diagnostics.ToArray();

    /// <summary>
    /// Starts the server, negotiates, and lists its tools. Never throws for an ordinary failure —
    /// a server that will not start is a state the UI shows, not an exception it must catch.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await DisconnectAsync().ConfigureAwait(false);

        State = ServerConnectionState.Connecting;
        FailureReason = null;
        NegotiatedProtocolVersion = null;

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = Descriptor.DisplayName,
                Command = Descriptor.Command,
                Arguments = [.. Descriptor.Arguments],
                WorkingDirectory = Descriptor.WorkingDirectory,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = RecordDiagnostic,
            });

            _client = await McpClient.CreateAsync(transport, ClientOptions(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _tools = [.. tools.Select(Map)];
            NegotiatedProtocolVersion = _client.NegotiatedProtocolVersion;
            ServerTitle = TextSanitizer.Clean(_client.ServerInfo?.Title ?? _client.ServerInfo?.Name,
                TextSanitizer.NameLength);
            State = ServerConnectionState.Connected;
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync().ConfigureAwait(false);
            State = ServerConnectionState.NotConnected;
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectAsync().ConfigureAwait(false);
            State = ServerConnectionState.Failed;
            FailureReason = Describe(ex);
            _tools = [];
        }
    }

    /// <summary>Calls a tool with no arguments at all.</summary>
    public Task<ToolCallOutcome> CallAsync(ToolInfo tool, CancellationToken cancellationToken = default)
        => CallAsync(tool, null, cancellationToken);

    /// <summary>
    /// Calls a tool with the arguments a generated form assembled (ROADMAP §10, Phase 3). The
    /// object arrives from <see cref="Schema.FormState"/> and is passed through as it stands —
    /// nothing here second-guesses it, because the schema engine has already decided what a filled
    /// in form means and the server is the authority on the rest.
    /// </summary>
    public async Task<ToolCallOutcome> CallAsync(
        ToolInfo tool, JsonObject? arguments, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (_client is null || State != ServerConnectionState.Connected)
            return ToolCallOutcome.Failure($"Not connected to {Descriptor.DisplayName}.");

        try
        {
            // NoArguments rather than null: an absent `arguments` member is not the same wire
            // shape as an empty object, and real servers reject the former. Verified against
            // @modelcontextprotocol/server-everything, which answers a zero-argument call with
            // "expected object, received undefined" if the member is omitted.
            var result = await _client
                .CallToolAsync(tool.Name, Marshal(arguments), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return MapResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolCallOutcome.Failure($"{tool.DisplayName} could not be called. {Describe(ex)}");
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task DisconnectAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client is null) return;

        try
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Shutting down a child process that has already died is not news.
            RecordDiagnostic("Disconnect: " + ex.Message);
        }
    }

    /// <summary>
    /// The argument object as the SDK wants it. Each value crosses as a <see cref="JsonElement"/>
    /// rather than as a live <see cref="JsonNode"/>: the form on screen still owns its nodes, and a
    /// request being serialised on a background thread must not be reading an object the user is
    /// still typing into.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> Marshal(JsonObject? arguments)
    {
        if (arguments is null || arguments.Count == 0) return NoArguments;

        var marshalled = new Dictionary<string, object?>(arguments.Count, StringComparer.Ordinal);

        foreach (var (name, value) in arguments)
            marshalled[name] = JsonSerializer.SerializeToElement(value);

        return marshalled;
    }

    private static McpClientOptions ClientOptions() => new()
    {
        ClientInfo = new Implementation
        {
            Name = "humcp",
            Title = "humcp",
            Version = typeof(ServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        },

        // No elicitation capability yet: MRTR arrives in Phase 5, and claiming a capability the app
        // cannot honour is worse than not claiming it. Sampling and roots are deprecated (§4,
        // pitfall 8) and are never claimed.
        Capabilities = new ClientCapabilities(),
    };

    private void RecordDiagnostic(string line)
    {
        _diagnostics.Enqueue(TextSanitizer.Clean(line, TextSanitizer.HelpLength));
        while (_diagnostics.Count > DiagnosticLineLimit) _diagnostics.TryDequeue(out _);
    }

    private string Describe(Exception exception)
    {
        var message = exception switch
        {
            McpProtocolException protocol => $"{protocol.Message} ({protocol.ErrorCode})",
            McpException mcp => mcp.Message,
            _ => exception.Message,
        };

        var cleaned = TextSanitizer.Clean(message, TextSanitizer.HelpLength);
        var lastDiagnostic = _diagnostics.LastOrDefault();

        // The child's own last words are usually the real explanation — "command not found",
        // "missing module" — and the transport exception is only the symptom.
        return string.IsNullOrWhiteSpace(lastDiagnostic) ? cleaned : $"{cleaned} Server said: {lastDiagnostic}";
    }

    // ------------------------------------------------------------------ protocol → neutral

    private static ToolInfo Map(McpClientTool tool)
    {
        return new ToolInfo(
            Name: TextSanitizer.Clean(tool.Name, TextSanitizer.NameLength),
            DisplayName: NameHumanizer.DisplayName(tool.Title, tool.Name),
            Summary: TextSanitizer.FirstSentence(tool.Description),
            FullDescription: TextSanitizer.CleanMultiline(tool.Description),
            Safety: Map(tool.ProtocolTool.Annotations),

            // Built here rather than when a form is opened, for two reasons. It detaches the schema
            // from the document the SDK owns, so a ToolInfo outlives the listing it came from; and
            // it means a hostile schema is defused once, at the edge, rather than on whatever thread
            // happens to open a window later.
            Form: FormBuilder.Build(tool.JsonSchema));
    }

    private static OperationSafety Map(ToolAnnotations? annotations)
        => annotations is null
            ? OperationSafety.Unstated
            : new OperationSafety(annotations.ReadOnlyHint, annotations.DestructiveHint, annotations.IdempotentHint);

    private static ToolCallOutcome MapResult(CallToolResult result)
    {
        var blocks = new List<ResultBlock>();

        foreach (var block in result.Content ?? [])
        {
            blocks.Add(block switch
            {
                TextContentBlock text =>
                    new TextResultBlock(TextSanitizer.CleanMultiline(text.Text)),

                // §4, pitfall 6: ImageContent has no alt text, so the honest presentation is the
                // object itself. Phase 4 adds Save and Open; v2 adds generated alt text.
                ImageContentBlock image =>
                    new ObjectResultBlock(Describe("Image", image.MimeType, image.Data.Length)),

                AudioContentBlock audio =>
                    new ObjectResultBlock(Describe("Audio", audio.MimeType, audio.Data.Length)),

                ResourceLinkBlock link =>
                    new ObjectResultBlock(TextSanitizer.Clean(
                        $"Resource link: {link.Title ?? link.Name}. {link.Uri}", TextSanitizer.HelpLength)),

                EmbeddedResourceBlock embedded =>
                    new ObjectResultBlock(TextSanitizer.Clean(
                        $"Embedded resource: {embedded.Resource?.Uri}", TextSanitizer.HelpLength)),

                _ => (ResultBlock)new ObjectResultBlock(
                    TextSanitizer.Clean($"Result part of type {block.Type}, not yet rendered.",
                        TextSanitizer.HelpLength)),
            });
        }

        // §4, pitfall 8: a result carrying anything other than "complete" is not something Phase 1
        // understands, and pretending otherwise would show the user a half-answer as a whole one.
        if (result.ResultType is { Length: > 0 } and not "complete")
        {
            return new ToolCallOutcome(
                IsError: true,
                Blocks: [new TextResultBlock(
                    $"The server answered with resultType \"{TextSanitizer.Clean(result.ResultType, 60)}\", " +
                    "which this build does not handle yet. Multi round-trip requests arrive in Phase 5.")],
                NeedsMoreInput: result.ResultType == "input_required");
        }

        return new ToolCallOutcome(
            IsError: result.IsError == true,
            Blocks: blocks,
            StructuredJson: result.StructuredContent?.ToString());
    }

    private static string Describe(string kind, string? mimeType, int bytes)
    {
        var type = TextSanitizer.Clean(mimeType, 60);
        var size = bytes switch
        {
            < 1024 => $"{bytes} bytes",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
        };

        return type.Length > 0 ? $"{kind}, {type}, {size}" : $"{kind}, {size}";
    }
}
