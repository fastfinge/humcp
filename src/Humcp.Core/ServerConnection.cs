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
    string? FailureReason = null)
{
    /// <summary>The configuration key the tree files the server's node under. Never spoken.</summary>
    public string Id { get; init; } = DisplayName;

    /// <summary>False for a server the configuration marks disabled, which is not started at all.</summary>
    public bool Enabled { get; init; } = true;
}

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

    /// <summary>
    /// The window that should show a question, for the duration of one call.
    /// </summary>
    /// <remarks>
    /// The SDK drives the whole multi round-trip exchange inside <c>CallToolAsync</c>: an
    /// <c>input_required</c> result never reaches this code, it is resolved through the registered
    /// handler and the original request is retried with a new id (phase5-notes 3.1). The handler is
    /// registered once, per connection - but a question belongs to the window whose call provoked
    /// it, and one connection can have several tool windows calling at once. An
    /// <see cref="AsyncLocal{T}"/> set inside <see cref="CallAsync"/> is what carries that: the
    /// handler runs on a pool thread, inside the calling flow, and reads the value the caller set.
    /// Measured rather than assumed; phase5-notes 3.1 records the probe.
    /// </remarks>
    private readonly AsyncLocal<ElicitationPrompter?> _prompter = new();

    private McpClient? _client;
    private IReadOnlyList<ToolInfo> _tools = [];

    /// <summary>The held-open <c>subscriptions/listen</c> request, cancelled on disconnect.</summary>
    private CancellationTokenSource? _listening;

    /// <summary>
    /// Re-listing after <c>tools/list_changed</c>: one at a time, and a notification that arrives
    /// while one is running asks for one more rather than for one each — a server adding ten tools
    /// in a loop is one new list, not ten.
    /// </summary>
    private readonly SemaphoreSlim _relisting = new(1, 1);
    private int _relistWanted;

    public ServerConnection(ServerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Descriptor = descriptor;
    }

    /// <summary>
    /// What this connection starts. Replaced by <see cref="ReconfigureAsync"/> rather than by making
    /// a new connection, so that every window holding this one — a tool form part filled in, say —
    /// goes on working against the edited server instead of a stale one.
    /// </summary>
    public ServerDescriptor Descriptor { get; private set; }

    public ServerConnectionState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            RaiseChanged();
        }
    }

    private ServerConnectionState _state = ServerConnectionState.NotConnected;

    /// <summary>
    /// Raised when anything a <see cref="Snapshot"/> carries has changed — state, tools, failure.
    /// On whatever thread the change happened on, which is usually not the UI's.
    /// </summary>
    public event EventHandler? Changed;

    public string? FailureReason { get; private set; }

    public string? NegotiatedProtocolVersion { get; private set; }

    /// <summary>The server's own title, once it has said one. Untrusted, so already sanitised.</summary>
    public string? ServerTitle { get; private set; }

    public IReadOnlyList<ToolInfo> Tools => _tools;

    public ServerSnapshot Snapshot() => new(
        Descriptor.DisplayName, State, _tools, NegotiatedProtocolVersion, FailureReason)
    {
        Id = Descriptor.Id,
        Enabled = Descriptor.Enabled,
    };

    /// <summary>
    /// Points this connection at an edited configuration entry. Disconnects first; reconnecting is
    /// the caller's decision, because a server just disabled must not be started.
    /// </summary>
    public async Task ReconfigureAsync(ServerDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        await DisconnectAsync().ConfigureAwait(false);

        Descriptor = descriptor;
        _tools = [];
        FailureReason = null;
        NegotiatedProtocolVersion = null;
        ServerTitle = null;
        _state = ServerConnectionState.NotConnected;
        RaiseChanged();
    }

    /// <summary>Stops the server process, and says so in the tree.</summary>
    public async Task StopAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);

        _tools = [];
        FailureReason = null;
        NegotiatedProtocolVersion = null;
        _state = ServerConnectionState.NotConnected;
        RaiseChanged();
    }

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

        FailureReason = null;
        NegotiatedProtocolVersion = null;
        _tools = [];

        if (Descriptor.Url is { Length: > 0 })
        {
            // Read from the file and kept in it, so a pasted configuration survives a save; not
            // connected to, because the Streamable HTTP transport is Phase 7's (ROADMAP §10).
            FailureReason = "humcp cannot connect to a server by web address yet, only to one it starts itself.";
            _state = ServerConnectionState.Failed;
            RaiseChanged();
            return;
        }

        State = ServerConnectionState.Connecting;

        try
        {
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = Descriptor.DisplayName,
                Command = Descriptor.Command,
                Arguments = [.. Descriptor.Arguments],
                WorkingDirectory = Descriptor.WorkingDirectory,
                EnvironmentVariables = Descriptor.Environment.ToDictionary(e => e.Key, e => (string?)e.Value),
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = RecordDiagnostic,
            });

            _client = await McpClient.CreateAsync(transport, ClientOptions(), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

            _tools = [.. tools.Select(Map)];
            Listen(_client);
            NegotiatedProtocolVersion = _client.NegotiatedProtocolVersion;
            ServerTitle = TextSanitizer.Clean(_client.ServerInfo?.Title ?? _client.ServerInfo?.Name,
                TextSanitizer.NameLength);
            State = ServerConnectionState.Connected;
        }
        catch (OperationCanceledException)
        {
            await DisconnectAsync().ConfigureAwait(false);
            _tools = [];
            State = ServerConnectionState.NotConnected;
            throw;
        }
        catch (Exception ex)
        {
            await DisconnectAsync().ConfigureAwait(false);

            // Set before the state, which is what raises Changed: a listener must never see Failed
            // without the reason.
            FailureReason = Describe(ex);
            _tools = [];
            State = ServerConnectionState.Failed;
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Calls a tool with no arguments at all.</summary>
    public Task<ToolCallOutcome> CallAsync(ToolInfo tool, CancellationToken cancellationToken = default)
        => CallAsync(tool, null, null, cancellationToken);

    /// <summary>Calls a tool with arguments and no way to ask the user anything.</summary>
    public Task<ToolCallOutcome> CallAsync(
        ToolInfo tool, JsonObject? arguments, CancellationToken cancellationToken = default)
        => CallAsync(tool, arguments, null, cancellationToken);

    /// <summary>
    /// Calls a tool with the arguments a generated form assembled (ROADMAP §10, Phase 3). The
    /// object arrives from <see cref="Schema.FormState"/> and is passed through as it stands —
    /// nothing here second-guesses it, because the schema engine has already decided what a filled
    /// in form means and the server is the authority on the rest.
    /// </summary>
    public Task<ToolCallOutcome> CallAsync(
        ToolInfo tool,
        JsonObject? arguments,
        ElicitationPrompter? elicit,
        CancellationToken cancellationToken = default)
        => CallAsync(tool, arguments, elicit, null, cancellationToken);

    /// <summary>
    /// The whole call: arguments, a window to ask questions in, and somewhere to report progress
    /// (§4, pitfall 9). Progress arrives on whatever thread the SDK read it on.
    /// </summary>
    public async Task<ToolCallOutcome> CallAsync(
        ToolInfo tool,
        JsonObject? arguments,
        ElicitationPrompter? elicit,
        IProgress<ToolProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tool);

        if (_client is null || State != ServerConnectionState.Connected)
            return ToolCallOutcome.Failure($"Not connected to {Descriptor.DisplayName}.");

        // Set before the await, so that it flows into whatever thread the SDK resolves an input
        // request on and no further: two windows calling one server each get their own.
        _prompter.Value = elicit;

        try
        {
            // NoArguments rather than null: an absent `arguments` member is not the same wire
            // shape as an empty object, and real servers reject the former. Verified against
            // @modelcontextprotocol/server-everything, which answers a zero-argument call with
            // "expected object, received undefined" if the member is omitted.
            var result = await _client
                .CallToolAsync(tool.Name, Marshal(arguments), Progress(progress), cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return MapResult(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnsupportedInputRequestException ex)
        {
            // Neither a transport failure nor the tool's fault: the server asked for something this
            // client never claimed. Said in humcp's own words, because the SDK's are method names.
            return ToolCallOutcome.Failure(
                $"{tool.DisplayName} asked humcp for {ex.What}, which it does not do, so the call stopped there.");
        }
        catch (Exception ex)
        {
            return ToolCallOutcome.Failure($"{tool.DisplayName} could not be called. {Describe(ex)}");
        }
        finally
        {
            _prompter.Value = null;
        }
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    /// <summary>The SDK's progress, as the neutral record the App sees, its message sanitised on the way.</summary>
    private static IProgress<ProgressNotificationValue>? Progress(IProgress<ToolProgress>? progress)
        => progress is null
            ? null
            : new SynchronousProgress(value => progress.Report(new ToolProgress(
                value.Progress, value.Total, TextSanitizer.Clean(value.Message, TextSanitizer.HelpLength))));

    /// <summary>
    /// Forwards on the reporting thread. <see cref="Progress{T}"/> would post to whatever
    /// synchronisation context built it, which here is none, and the App marshals for itself anyway.
    /// </summary>
    private sealed class SynchronousProgress(Action<ProgressNotificationValue> report) : IProgress<ProgressNotificationValue>
    {
        public void Report(ProgressNotificationValue value) => report(value);
    }

    private async Task DisconnectAsync()
    {
        var listening = Interlocked.Exchange(ref _listening, null);
        if (listening is not null)
        {
            await listening.CancelAsync().ConfigureAwait(false);
            listening.Dispose();
        }

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

    private McpClientOptions ClientOptions() => new()
    {
        ClientInfo = new Implementation
        {
            Name = "humcp",
            Title = "humcp",
            Version = typeof(ServerConnection).Assembly.GetName().Version?.ToString(3) ?? "0.1.0",
        },

        // Form mode, and nothing else. url mode exists to collect what the client must never see,
        // which is the OAuth-shaped problem ROADMAP §11.3 defers to v3; declaring it would mean
        // handing a server-chosen address to a browser in the middle of a tool call. Sampling and
        // roots are deprecated (§4, pitfall 8) and are never claimed either.
        Capabilities = new ClientCapabilities
        {
            Elicitation = new ElicitationCapability { Form = new FormElicitationCapability() },
        },

        // All three handlers are registered, including the two humcp cannot do. A handler that is
        // missing makes the SDK throw with a message about handler registration; a handler that
        // refuses makes it throw with a message about the user's tool (phase5-notes 3.4).
        // Both of the refusals are deprecated APIs, and refusing them is exactly why they are
        // referenced: a client that stops compiling against a deprecated request is a client that
        // stops being able to turn one down.
#pragma warning disable MCP9005
        Handlers = new McpClientHandlers
        {
            // A server on an older revision sends this unprompted; one on 2026-07-28 sends it down
            // the subscriptions/listen stream opened in Listen. The same handler hears both.
            NotificationHandlers =
            [
                new(NotificationMethods.ToolListChangedNotification, (_, _) =>
                {
                    _ = RelistAsync();
                    return ValueTask.CompletedTask;
                }),
            ],

            ElicitationHandler = OnElicitAsync,
            SamplingHandler = (_, _, _) => throw new UnsupportedInputRequestException("a language model"),
            RootsHandler = (_, _) => throw new UnsupportedInputRequestException(
                "a list of the folders it is allowed to read"),
        },
#pragma warning restore MCP9005
    };

    // ------------------------------------------------------------------ the tool list changing

    /// <summary>
    /// Asks a 2026-07-28 server to say when its tool list changes. That revision removed the
    /// unsolicited channel older ones used (SEP-2575): a notification arrives only down the response
    /// stream of a <c>subscriptions/listen</c> request, held open for the life of the connection.
    /// </summary>
    /// <remarks>
    /// The SDK has no client call for this, so it is sent as a plain request whose answer never
    /// comes; it ends when the connection does. Only sent to a server that both speaks the revision
    /// and says it publishes list changes, because asking anything else is an error in the
    /// diagnostics for nothing.
    /// </remarks>
    private void Listen(McpClient client)
    {
        if (client.ServerCapabilities?.Tools?.ListChanged != true) return;
        if (string.CompareOrdinal(client.NegotiatedProtocolVersion, "2026-07-28") < 0) return;

        var listening = new CancellationTokenSource();
        _listening = listening;

        var request = new JsonRpcRequest
        {
            Method = RequestMethods.SubscriptionsListen,
            Params = JsonSerializer.SerializeToNode(
                new SubscriptionsListenRequestParams
                {
                    Notifications = new SubscriptionsListenNotifications { ToolsListChanged = true },
                },
                McpJsonUtilities.DefaultOptions),
        };

        _ = Task.Run(async () =>
        {
            try
            {
                await client.SendRequestAsync(request, listening.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The connection closing, which is the only ordinary way this ends.
            }
            catch (Exception ex)
            {
                RecordDiagnostic("The server would not keep a subscription open, so humcp will not hear when its tools change: " +
                                 ex.Message);
            }
        });
    }

    /// <summary>Lists the tools again, coalescing a burst of notifications into one listing.</summary>
    private async Task RelistAsync()
    {
        Interlocked.Exchange(ref _relistWanted, 1);

        if (!await _relisting.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            while (Interlocked.Exchange(ref _relistWanted, 0) == 1)
            {
                if (_client is not { } client || State != ServerConnectionState.Connected) return;

                var tools = await client.ListToolsAsync().ConfigureAwait(false);
                _tools = [.. tools.Select(Map)];

                RecordDiagnostic($"The server said its tools changed. It now publishes {_tools.Count}.");
                RaiseChanged();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RecordDiagnostic("The server said its tools changed, and listing them again failed: " + ex.Message);
        }
        finally
        {
            _relisting.Release();
        }

        // A notification that landed between the last check and the release would otherwise wait
        // for the next one to be noticed.
        if (Volatile.Read(ref _relistWanted) == 1) await RelistAsync().ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ being asked something

    /// <summary>
    /// A server's question, on its way to a window. Everything crossing out of here is neutral
    /// (§6.1): the schema has already become a <see cref="FormModel"/>, and the answer comes back as
    /// an <see cref="ElicitationAnswer"/>.
    /// </summary>
    private async ValueTask<ElicitResult> OnElicitAsync(
        ElicitRequestParams? request, CancellationToken cancellationToken)
    {
        var prompter = _prompter.Value;

        if (prompter is null)
        {
            // Nowhere to show it. Declining is the honest answer - the tool is told that nobody
            // answered - and the diagnostic is what makes it findable afterwards.
            RecordDiagnostic("A question arrived during a call that had nowhere to show one. Declined.");
            return new ElicitResult { Action = "decline" };
        }

        var answer = await prompter(Describe(request), cancellationToken).ConfigureAwait(false);

        return new ElicitResult
        {
            Action = answer.Action switch
            {
                ElicitationAction.Accept => "accept",
                ElicitationAction.Decline => "decline",
                _ => "cancel",
            },

            // Only an acceptance carries values. The spec says content is "typically omitted" for
            // the other two, and an empty object would be an answer where there is none.
            Content = answer.Action == ElicitationAction.Accept ? Content(answer.Content) : null,
        };
    }

    private static ElicitationRequest Describe(ElicitRequestParams? request)
    {
        var isUrl = string.Equals(request?.Mode, "url", StringComparison.Ordinal);

        return new ElicitationRequest
        {
            Kind = isUrl ? ElicitationKind.WebPage : ElicitationKind.Form,
            Message = TextSanitizer.CleanMultiline(request?.Message, TextSanitizer.HelpLength),
            Form = isUrl || request?.RequestedSchema is null
                ? FormModel.Empty
                : FormBuilder.Build(RequestedSchema(request)),
            Url = isUrl ? TextSanitizer.Clean(request?.Url, TextSanitizer.NameLength) : null,
        };
    }

    /// <summary>
    /// The requested schema as JSON, which is what the schema engine takes. Round-tripped through
    /// the SDK's own serialiser rather than read off the typed objects, so that what a form is
    /// generated from is exactly what the server put on the wire.
    /// </summary>
    private static JsonElement RequestedSchema(ElicitRequestParams? request)
    {
        if (request?.RequestedSchema is not { } schema) return default;

        try
        {
            return JsonSerializer.SerializeToElement(schema, McpJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // FormBuilder never fails to render and neither does this: an undefined element is a
            // form with no fields on it, which is still a window with three answers in it.
            return default;
        }
    }

    /// <summary>
    /// A filled-in form as the protocol wants it, one <see cref="JsonElement"/> per property, and
    /// detached from the live nodes for the same reason arguments are (see <see cref="Marshal"/>).
    /// </summary>
    private static Dictionary<string, JsonElement>? Content(JsonObject? content)
    {
        if (content is null) return null;

        var values = new Dictionary<string, JsonElement>(content.Count, StringComparer.Ordinal);

        foreach (var (name, value) in content)
            values[name] = JsonSerializer.SerializeToElement(value);

        return values;
    }

    private void RecordDiagnostic(string line)
    {
        _diagnostics.Enqueue(TextSanitizer.Clean(line, TextSanitizer.HelpLength));
        while (_diagnostics.Count > DiagnosticLineLimit) _diagnostics.TryDequeue(out _);
    }

    private string Describe(Exception exception)
    {
        var message = exception switch
        {
            _ when RunawayRoundTrips(exception) => RunawayMessage,
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
            Form: FormBuilder.Build(tool.JsonSchema),

            // Carried, not interpreted. What a result means needs the value as well, and the two
            // meet in the renderer rather than here (ROADMAP §6.1, hedge 5).
            OutputSchema: tool.ProtocolTool.OutputSchema);
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
                TextContentBlock text => Text(text.Text),

                // §4, pitfall 6: nothing in the protocol carries alt text, so what crosses is the
                // bytes and what is known about them. Phase 4's window offers Save and Open; v2's
                // IResultSummarizer seam is where a description would come from.
                ImageContentBlock image =>
                    new MediaResultBlock(MediaKind.Image, Mime(image.MimeType), image.DecodedData),

                AudioContentBlock audio =>
                    new MediaResultBlock(MediaKind.Audio, Mime(audio.MimeType), audio.DecodedData),

                ResourceLinkBlock link => new LinkResultBlock(
                    Uri: TextSanitizer.Clean(link.Uri, TextSanitizer.NameLength),
                    Label: NameHumanizer.DisplayName(link.Title, link.Name),
                    Description: TextSanitizer.FirstSentence(link.Description),
                    MimeType: Mime(link.MimeType),
                    Size: link.Size),

                EmbeddedResourceBlock embedded => Embedded(embedded),

                _ => Unknown(block),
            });
        }

        // §4, pitfall 8: a result carrying anything other than "complete" is not something this
        // build understands, and pretending otherwise would show the user a half-answer as a whole
        // one. "input_required" is no longer one of these — the SDK resolves it and retries before
        // this method is reached, so a call that needed three round trips arrives here as one
        // complete answer (phase5-notes 3.1).
        if (result.ResultType is { Length: > 0 } and not "complete")
        {
            return new ToolCallOutcome(
                IsError: true,
                Blocks: [new TextResultBlock(
                    $"The server answered with resultType \"{TextSanitizer.Clean(result.ResultType, 60)}\", " +
                    "which this build does not understand.")]);
        }

        return new ToolCallOutcome(
            IsError: result.IsError == true,
            Blocks: blocks,
            Structured: Structured(result));
    }

    /// <summary>
    /// Text as a result rather than as a description: line structure kept (§7.19), and capped at a
    /// length real answers do not reach — with the original length carried so that a result which
    /// did reach it can say so.
    /// </summary>
    private static TextResultBlock Text(string? text)
    {
        var cleaned = TextSanitizer.CleanMultiline(text, TextSanitizer.ResultLength);
        return new TextResultBlock(cleaned, text?.Length ?? 0);
    }

    /// <summary>
    /// An embedded resource, which arrives as one of two quite different things: text, which is
    /// text, and a blob, which is a file.
    /// </summary>
    private static ResultBlock Embedded(EmbeddedResourceBlock embedded) => embedded.Resource switch
    {
        TextResourceContents text => new ResourceTextResultBlock(
            TextSanitizer.Clean(text.Uri, TextSanitizer.NameLength),
            Mime(text.MimeType),
            TextSanitizer.CleanMultiline(text.Text, TextSanitizer.ResultLength)),

        BlobResourceContents blob => new MediaResultBlock(
            MediaKind.File,
            Mime(blob.MimeType),
            blob.DecodedData,
            TextSanitizer.Clean(blob.Uri, TextSanitizer.NameLength)),

        _ => new UnknownResultBlock("resource", "(the server sent a resource with no contents)"),
    };

    /// <summary>
    /// A part of a kind this build has never met. Shown as JSON rather than dropped: the protocol
    /// is a moving target (§4, pitfall 8) and an unknown part is news, not noise.
    /// </summary>
    private static UnknownResultBlock Unknown(ContentBlock block)
    {
        string json;

        try
        {
            json = JsonSerializer.Serialize(block, McpJsonUtilities.DefaultOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            json = $"(this part could not be read: {ex.Message})";
        }

        return new UnknownResultBlock(
            TextSanitizer.Clean(block.Type, 60),
            TextSanitizer.CleanMultiline(json, TextSanitizer.DetailsLength));
    }

    /// <summary>
    /// The structured content, detached from the response it arrived in: the response is free to be
    /// dropped the moment this method returns, and a window may be reading the value minutes later.
    /// </summary>
    private static JsonElement? Structured(CallToolResult result) =>
        result.StructuredContent is { ValueKind: not JsonValueKind.Undefined } content
            ? content.Clone()
            : null;

    private static string Mime(string? mimeType) => TextSanitizer.Clean(mimeType, 60);

    private const string RunawayMessage =
        "The server kept asking for more input without ever finishing, and humcp stopped after ten rounds.";

    /// <summary>
    /// A server that never completes a multi round-trip exchange. The SDK stops at ten and says so
    /// in its own vocabulary; every message humcp shows is spoken, so this one is rewritten.
    /// </summary>
    /// <remarks>
    /// Recognised by the SDK's own sentence, which is as brittle as it looks and deliberately
    /// harmless: when the wording changes the user gets the SDK's sentence instead of ours, which
    /// is a worse sentence and not a worse outcome. There is nothing better to key on — the
    /// exception carries no code of its own — and counting round trips here is not possible,
    /// because a round trip that asks for nothing never reaches this client (phase5-notes 3.5).
    /// </remarks>
    private static bool RunawayRoundTrips(Exception exception)
        => exception is McpException
           && exception.Message.Contains("InputRequiredResult", StringComparison.Ordinal);
}
