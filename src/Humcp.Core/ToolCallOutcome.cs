namespace Humcp.Core;

/// <summary>
/// One piece of a result. Phase 1 renders text and describes everything else as an object;
/// Phase 4 gives each kind a real presentation (ROADMAP §10).
/// </summary>
public abstract record ResultBlock;

/// <summary>Text, ready for the read-only result box (ROADMAP §7.9).</summary>
public sealed record TextResultBlock(string Text) : ResultBlock;

/// <summary>
/// Something Phase 1 does not render — an image, audio, an embedded or linked resource. Described
/// rather than dropped, because a tool that returns a chart must not leave the user with nothing
/// (ROADMAP §4, pitfall 6).
/// </summary>
public sealed record ObjectResultBlock(string Description) : ResultBlock;

/// <summary>
/// The outcome of one call, with the protocol left behind. <see cref="StructuredJson"/> is carried
/// but not interpreted: Phase 4 pairs it with the tool's output schema and renders a grid.
/// </summary>
public sealed record ToolCallOutcome(
    bool IsError,
    IReadOnlyList<ResultBlock> Blocks,
    string? StructuredJson = null,
    bool NeedsMoreInput = false)
{
    /// <summary>A failure that never reached the server, or came back as a transport-level error.</summary>
    public static ToolCallOutcome Failure(string message)
        => new(true, [new TextResultBlock(message)]);
}
