using System.Text.Json.Nodes;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>What a server is asking for in the middle of a tool call.</summary>
public enum ElicitationKind
{
    /// <summary>A form to fill in. The only kind humcp declares (ROADMAP §10, Phase 5).</summary>
    Form,

    /// <summary>
    /// A web page to go to and come back from. Declared by nobody here: url mode exists to collect
    /// what the client must never see — credentials, card numbers, an OAuth code — and humcp does
    /// not open a server's address as a side effect of running a tool. It still arrives, so it
    /// still has to be explained rather than ignored.
    /// </summary>
    WebPage,
}

/// <summary>
/// One question a tool call stopped to ask, in the terms the shell needs to show it (ROADMAP §6.1:
/// nothing crossing out of <c>Humcp.Core</c> carries MCP vocabulary).
/// </summary>
/// <remarks>
/// The schema arrives as a restricted subset — flat objects of primitives, with enumerations either
/// bare, titled, or multi-select — and goes through the same <see cref="FormBuilder"/> as a tool's
/// own input schema. There is no second form generator, and phase5-notes 3.2 is the measurement
/// that says there does not need to be one.
/// </remarks>
public sealed record ElicitationRequest
{
    public required ElicitationKind Kind { get; init; }

    /// <summary>Why the server is asking. Untrusted, so already sanitised.</summary>
    public required string Message { get; init; }

    /// <summary>The fields to show. Empty for a question that is only a question.</summary>
    public required FormModel Form { get; init; }

    /// <summary>Where the server wanted to send the user. Set only for <see cref="ElicitationKind.WebPage"/>.</summary>
    public string? Url { get; init; }

    /// <summary>Which of the requests in one round trip this is. Diagnostics only.</summary>
    public string Key { get; init; } = "";

    public bool HasFields => Form.Fields.Count > 0;
}

/// <summary>
/// The three answers the protocol has. All three are answers: a tool that is declined has been
/// told something, and only <see cref="Accept"/> carries values.
/// </summary>
public enum ElicitationAction
{
    /// <summary>The user filled it in and said yes.</summary>
    Accept,

    /// <summary>The user said no. The tool is expected to carry on without it.</summary>
    Decline,

    /// <summary>The user dismissed the question without answering it either way.</summary>
    Cancel,
}

/// <summary>What came back from the user.</summary>
public sealed record ElicitationAnswer(ElicitationAction Action, JsonObject? Content = null)
{
    public static readonly ElicitationAnswer Declined = new(ElicitationAction.Decline);

    public static readonly ElicitationAnswer Cancelled = new(ElicitationAction.Cancel);

    public static ElicitationAnswer Accepted(JsonObject content) => new(ElicitationAction.Accept, content);
}

/// <summary>
/// How the shell shows a question and waits for the answer. Supplied per call rather than per
/// connection, because the window that has to open belongs to the window the call was made from.
/// </summary>
public delegate Task<ElicitationAnswer> ElicitationPrompter(
    ElicitationRequest request, CancellationToken cancellationToken);

/// <summary>
/// Thrown when a server asks the client to do something humcp has never claimed it can do.
/// </summary>
/// <remarks>
/// <para>
/// Sampling and roots are both deprecated in 2026-07-28 and neither is declared by this client
/// (ROADMAP §2: the human is the intelligence). A server can ask anyway, and the SDK resolves an
/// input request by dispatching it to a registered handler — so the way to decline is to register
/// one that refuses. Throwing rather than answering is deliberate: a sampling result has no
/// "no thank you" in it, and inventing a language model's reply would be a lie told to a server.
/// </para>
/// <para>
/// Caught in <see cref="ServerConnection"/> and turned into an ordinary failed call, with
/// <see cref="What"/> as the sentence the user hears.
/// </para>
/// </remarks>
public sealed class UnsupportedInputRequestException(string what)
    : Exception($"This tool asked humcp for {what}, which it does not do.")
{
    /// <summary>What the server asked for, in words rather than in method names.</summary>
    public string What { get; } = what;
}
