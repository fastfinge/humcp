using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// Sampling, roots and enumNames are all deprecated in 2026-07-28, and every one of them is here
// on purpose: a client has to keep behaving when a server asks for a thing the revision says is on
// its way out. Warned about once, here, rather than pretended away.
#pragma warning disable MCP9001, MCP9005

namespace Humcp.TestServer;

/// <summary>
/// The fixture's multi round-trip tools (ROADMAP §10, Phase 5). One per shape an MRTR exchange can
/// have, rather than one per scenario — the same rule the <c>result_</c> tools follow.
/// </summary>
/// <remarks>
/// <para>
/// Every one of these is stateless between round trips, which is what MRTR is for: the whole of the
/// server's memory travels in <c>requestState</c> and comes back on the retry. A client that drops
/// it, rewrites it, or answers the wrong round trip breaks these tools visibly rather than subtly.
/// </para>
/// <para>
/// The state here is plain JSON on purpose. A real server would encrypt it — the spec says so —
/// but a fixture whose state a reader can print is a fixture whose failures are diagnosable.
/// </para>
/// </remarks>
internal static class MrtrTools
{
    public static IEnumerable<McpServerTool> All()
    {
        yield return Book();
        yield return Confirm();
        yield return Kinds();
        yield return StateOnly();
        yield return Sampling();
        yield return Roots();
        yield return Url();
    }

    /// <summary>How many tools this file publishes.</summary>
    public const int Count = 7;

    // ------------------------------------------------------------------ the exit criterion

    /// <summary>
    /// Two round trips and then an answer, which is Phase 5's exit criterion in one tool: a form,
    /// a second form that depends on the first, and a result that could not have been produced
    /// without both.
    /// </summary>
    private static McpServerTool Book() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context,
         [Description("Where the journey ends.")] string destination) =>
        {
            var state = State(context);

            switch (Text(state, "step"))
            {
                case null:
                    throw new InputRequiredException(
                        new Dictionary<string, InputRequest>
                        {
                            ["journey"] = InputRequest.ForElicitation(new ElicitRequestParams
                            {
                                Mode = "form",
                                Message = $"When would you like to travel to {destination}?",
                                RequestedSchema = new ElicitRequestParams.RequestSchema
                                {
                                    Properties =
                                    {
                                        ["departure"] = new ElicitRequestParams.StringSchema
                                        {
                                            Title = "Departure date",
                                            Description = "The day the journey starts.",
                                            Format = "date",
                                        },
                                        ["passengers"] = new ElicitRequestParams.NumberSchema
                                        {
                                            Type = "integer",
                                            Title = "Passengers",
                                            Description = "How many people are travelling.",
                                            Minimum = 1,
                                            Maximum = 9,
                                        },
                                        ["flexible"] = new ElicitRequestParams.BooleanSchema
                                        {
                                            Title = "Flexible dates",
                                            Description = "Whether a day either side would do.",
                                            Default = false,
                                        },
                                    },
                                    Required = ["departure", "passengers"],
                                },
                            }),
                        },
                        requestState: Encode(new { step = "seat", destination }));

                case "seat":
                    var journey = Answer(context, "journey");
                    if (journey is null || !journey.IsAccepted)
                        return Refused("No booking was made", journey);

                    throw new InputRequiredException(
                        new Dictionary<string, InputRequest>
                        {
                            ["seat"] = InputRequest.ForElicitation(new ElicitRequestParams
                            {
                                Mode = "form",
                                Message = "Where would you like to sit?",
                                RequestedSchema = new ElicitRequestParams.RequestSchema
                                {
                                    Properties =
                                    {
                                        ["position"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
                                        {
                                            Title = "Seat position",
                                            OneOf =
                                            [
                                                new ElicitRequestParams.EnumSchemaOption
                                                    { Const = "window", Title = "By the window" },
                                                new ElicitRequestParams.EnumSchemaOption
                                                    { Const = "aisle", Title = "On the aisle" },
                                                new ElicitRequestParams.EnumSchemaOption
                                                    { Const = "any", Title = "No preference" },
                                            ],
                                        },
                                    },
                                    Required = ["position"],
                                },
                            }),
                        },
                        requestState: Encode(new
                        {
                            step = "done",
                            destination = Text(state, "destination") ?? destination,
                            departure = Field(journey, "departure"),
                            passengers = Field(journey, "passengers"),
                            flexible = Field(journey, "flexible"),
                        }));

                default:
                    var seat = Answer(context, "seat");
                    if (seat is null || !seat.IsAccepted)
                        return Refused("The seat was never chosen, so no booking was made", seat);

                    return string.Join(System.Environment.NewLine,
                        "Booked.",
                        "",
                        $"Destination: {Text(state, "destination")}",
                        $"Departure:   {Text(state, "departure")}",
                        $"Passengers:  {Text(state, "passengers")}",
                        $"Flexible:    {Text(state, "flexible")}",
                        $"Seat:        {Field(seat, "position")}",
                        "",
                        "Two round trips, and the server remembered nothing between them.");
            }
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_book",
            Title = "Book a journey",
            Description = "Asks two questions and then answers. The second question depends on the answer " +
                          "to the first, so a client that cannot carry request state cannot finish this.",
            ReadOnly = false,
        });

    // ------------------------------------------------------------------ one of each shape

    /// <summary>
    /// An elicitation with nothing to fill in: a question with three answers, which is what
    /// accept, decline and cancel are for. The shape a confirmation takes.
    /// </summary>
    private static McpServerTool Confirm() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is null)
            {
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["confirm"] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "form",
                            Message = "Nothing will be deleted. Shall this tool pretend to delete it anyway?",
                            RequestedSchema = new ElicitRequestParams.RequestSchema(),
                        }),
                    },
                    requestState: Encode(new { step = "done" }));
            }

            var answer = Answer(context, "confirm");

            return $"The answer was \"{answer?.Action ?? "(nothing came back)"}\". " +
                   "All three of accept, decline and cancel are answers, and none of them is a failure.";
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_confirm",
            Title = "Ask before doing it",
            Description = "Elicits with an empty schema, which is how a server asks a yes-or-no question. " +
                          "Reports which of the three actions came back.",
            ReadOnly = false,
        });

    /// <summary>
    /// Every kind of field the elicitation subset has, in one request. ROADMAP §3 claims Tier 1 is
    /// a superset of this subset; this is the tool that makes the claim checkable by ear as well as
    /// by assertion.
    /// </summary>
    private static McpServerTool Kinds() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is null)
            {
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["everything"] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "form",
                            Message = "One field of every kind the elicitation subset allows.",
                            RequestedSchema = EverySchemaKind(),
                        }),
                    },
                    requestState: Encode(new { step = "done" }));
            }

            var answer = Answer(context, "everything");

            if (answer is null || !answer.IsAccepted)
                return Refused("Nothing was filled in", answer);

            var lines = new List<string> { "What arrived:", "" };

            foreach (var pair in answer.Content ?? new Dictionary<string, JsonElement>())
                lines.Add($"{pair.Key,-12} {pair.Value.ValueKind,-6} {pair.Value.GetRawText()}");

            // And the response exactly as it came off the wire, which is not always the same thing
            // as what the client's code passed to its own SDK (phase5-notes 3.3).
            lines.Add("");
            lines.Add("Off the wire:");
            lines.Add(context.Params!.InputResponses!["everything"].RawValue.ToString());

            return string.Join(System.Environment.NewLine, lines);
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_kinds",
            Title = "One field of each kind",
            Description = "Elicits a form carrying every schema kind the 2026-07-28 elicitation subset " +
                          "permits, then reports back exactly what the client sent.",
            ReadOnly = true,
        });

    /// <summary>Every kind in <c>PrimitiveSchemaDefinition</c>, built through the SDK's own types.</summary>
    public static ElicitRequestParams.RequestSchema EverySchemaKind() => new()
    {
        Properties =
        {
            ["plain"] = new ElicitRequestParams.StringSchema
            {
                Title = "Plain text",
                Description = "A string with a length limit and nothing else.",
                MinLength = 2,
                MaxLength = 40,
            },
            ["email"] = new ElicitRequestParams.StringSchema { Title = "Email address", Format = "email" },
            ["website"] = new ElicitRequestParams.StringSchema { Title = "Website", Format = "uri" },
            ["day"] = new ElicitRequestParams.StringSchema { Title = "A date", Format = "date" },
            ["moment"] = new ElicitRequestParams.StringSchema { Title = "A date and time", Format = "date-time" },
            ["count"] = new ElicitRequestParams.NumberSchema
            {
                Type = "integer", Title = "A whole number", Minimum = 0, Maximum = 100, Default = 7,
            },
            ["ratio"] = new ElicitRequestParams.NumberSchema
            {
                Type = "number", Title = "A fractional number", Minimum = 0, Maximum = 1,
            },
            ["agreed"] = new ElicitRequestParams.BooleanSchema
            {
                Title = "A check box", Description = "It starts ticked.", Default = true,
            },
            ["size"] = new ElicitRequestParams.UntitledSingleSelectEnumSchema
            {
                Title = "Pick one, unlabelled",
                Enum = ["small", "medium", "large"],
                Default = "medium",
            },
            ["urgency"] = new ElicitRequestParams.TitledSingleSelectEnumSchema
            {
                Title = "Pick one, labelled",
                OneOf =
                [
                    new ElicitRequestParams.EnumSchemaOption { Const = "p0", Title = "Drop everything" },
                    new ElicitRequestParams.EnumSchemaOption { Const = "p1", Title = "This week" },
                    new ElicitRequestParams.EnumSchemaOption { Const = "p2", Title = "Whenever" },
                ],
            },
            ["colours"] = new ElicitRequestParams.UntitledMultiSelectEnumSchema
            {
                Title = "Pick several, unlabelled",
                Items = new ElicitRequestParams.UntitledEnumItemsSchema { Enum = ["red", "green", "blue"] },
                MinItems = 0,
                MaxItems = 3,
            },
            ["regions"] = new ElicitRequestParams.TitledMultiSelectEnumSchema
            {
                Title = "Pick several, labelled",
                Items = new ElicitRequestParams.TitledEnumItemsSchema
                {
                    AnyOf =
                    [
                        new ElicitRequestParams.EnumSchemaOption { Const = "eu-west-1", Title = "Ireland" },
                        new ElicitRequestParams.EnumSchemaOption { Const = "us-east-1", Title = "Virginia" },
                    ],
                },
            },
            ["legacy"] = new ElicitRequestParams.LegacyTitledEnumSchema
            {
                Title = "Pick one, labelled the old way",
                Enum = ["asc", "desc"],
                EnumNames = ["Ascending", "Descending"],
            },
        },
        Required = ["plain", "urgency"],
    };

    /// <summary>
    /// A round trip that asks for nothing at all — the load-shedding case the SDK documents. The
    /// client has to retry with the state echoed back and no responses, which is the one exchange
    /// where nothing is shown to the user.
    /// </summary>
    private static McpServerTool StateOnly() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context,
         [Description("How many times to come back before answering.")] int rounds = 2) =>
        {
            var state = State(context);
            var round = Number(state, "round") ?? 0;
            var wanted = Number(state, "rounds") ?? Math.Clamp(rounds, 0, 5000);

            if (round < wanted)
            {
                throw new InputRequiredException(
                    inputRequests: null, requestState: Encode(new { round = round + 1, rounds = wanted }));
            }

            return $"Came back {round} times with nothing to ask, and the state survived every one.";
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_state_only",
            Title = "Come back in a moment",
            Description = "Answers twice with request state and no questions, which is what a busy server " +
                          "does. Nothing is shown to the user; the call simply takes three round trips.",
            ReadOnly = true,
            Idempotent = true,
        });

    /// <summary>
    /// Asks the client to run a language model. humcp has none (ROADMAP §2), never declares the
    /// capability, and has to decline this without falling over.
    /// </summary>
    private static McpServerTool Sampling() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is null)
            {
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["summary"] = InputRequest.ForSampling(new CreateMessageRequestParams
                        {
                            Messages =
                            [
                                new SamplingMessage
                                {
                                    Role = Role.User,
                                    Content = [new TextContentBlock { Text = "Summarise this in one sentence." }],
                                },
                            ],
                            MaxTokens = 100,
                        }),
                    },
                    requestState: Encode(new { step = "done" }));
            }

            return "The client answered a sampling request, which humcp is not supposed to be able to do.";
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_sampling",
            Title = "Ask for a language model",
            Description = "Requests sampling, which is deprecated and which this client does not offer. " +
                          "Exists so that declining is a tested path rather than a hoped-for one.",
            ReadOnly = true,
        });

    /// <summary>Asks for the client's roots. Deprecated, never declared, declined the same way.</summary>
    private static McpServerTool Roots() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is null)
            {
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["roots"] = InputRequest.ForRootsList(new ListRootsRequestParams()),
                    },
                    requestState: Encode(new { step = "done" }));
            }

            return "The client listed its roots, which it was never meant to have.";
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_roots",
            Title = "Ask for roots",
            Description = "Requests the client's roots, which are deprecated in this revision and which " +
                          "humcp does not publish.",
            ReadOnly = true,
        });

    /// <summary>
    /// Out-of-band elicitation: "open this web page and come back". The mode humcp does not claim,
    /// asked for anyway, because a capability that is not declared is still a request that can
    /// arrive.
    /// </summary>
    private static McpServerTool Url() => McpServerTool.Create(
        (RequestContext<CallToolRequestParams> context) =>
        {
            if (context.Params?.RequestState is null)
            {
                throw new InputRequiredException(
                    new Dictionary<string, InputRequest>
                    {
                        ["authorise"] = InputRequest.ForElicitation(new ElicitRequestParams
                        {
                            Mode = "url",
                            ElicitationId = "fixture-url-1",
                            Url = "https://example.com/authorise?token=not-a-real-token",
                            Message = "Sign in on the web to continue.",
                        }),
                    },
                    requestState: Encode(new { step = "done" }));
            }

            var answer = Answer(context, "authorise");
            return $"The answer to an out-of-band request was \"{answer?.Action ?? "(nothing came back)"}\".";
        },
        new McpServerToolCreateOptions
        {
            Name = "mrtr_url",
            Title = "Sign in on the web",
            Description = "Elicits in url mode, which sends the user out to a browser. humcp declares " +
                          "form mode only, so this is the request it has to turn down and explain.",
            ReadOnly = false,
        });

    // ------------------------------------------------------------------ plumbing

    private static string Encode(object state) => JsonSerializer.Serialize(state);

    private static JsonElement State(RequestContext<CallToolRequestParams> context)
        => context.Params?.RequestState is { Length: > 0 } text
            ? JsonDocument.Parse(text).RootElement
            : default;

    private static string? Text(JsonElement state, string name)
        => state.ValueKind == JsonValueKind.Object && state.TryGetProperty(name, out var value)
            ? value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText()
            : null;

    private static int? Number(JsonElement state, string name)
        => state.ValueKind == JsonValueKind.Object
           && state.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.Number
            ? value.GetInt32()
            : null;

    private static ElicitResult? Answer(RequestContext<CallToolRequestParams> context, string key)
        => context.Params?.InputResponses is { } responses && responses.TryGetValue(key, out var response)
            ? response.Deserialize(InputResponse.ElicitResultJsonTypeInfo)
            : null;

    private static string? Field(ElicitResult? answer, string name)
        => answer?.Content is { } content && content.TryGetValue(name, out var value)
            ? value.GetRawText()
            : null;

    private static string Refused(string what, ElicitResult? answer)
        => $"{what}: the client answered \"{answer?.Action ?? "(nothing came back)"}\".";
}
