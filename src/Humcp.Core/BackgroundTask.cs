using System.Globalization;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>Where a background task is, in humcp's words rather than the extension's.</summary>
public enum BackgroundTaskState
{
    /// <summary>The server is working on it.</summary>
    Working,

    /// <summary>The server has asked a question and is waiting for the answer.</summary>
    NeedsAnswer,

    /// <summary>Finished with a result, which may itself be the tool's error (<c>isError</c>).</summary>
    Completed,

    /// <summary>The server stopped it, usually because it was asked to.</summary>
    Stopped,

    /// <summary>The server reports that running it failed, as a protocol error rather than a result.</summary>
    Failed,

    /// <summary>humcp can no longer find out: the connection ended, or the server forgot the task.</summary>
    Lost,
}

/// <summary>
/// One tool call that the server chose to run as a task (the <c>io.modelcontextprotocol/tasks</c>
/// extension, ROADMAP §11, Phase 14): its result comes later, and humcp asks for it until it does.
/// </summary>
/// <remarks>
/// <para>
/// Neutral, as everything crossing out of Core is (§6.1): the App sees a state, a status line and,
/// at the end, the same <see cref="ToolCallOutcome"/> a call produces, and never the extension's
/// types. The connection that made it is the only thing that polls it.
/// </para>
/// <para>
/// A task outlives the window that started it. Closing that window stops waiting for the answer and
/// leaves the work running; <see cref="StopAsync"/> is what asks the server to stop it. The two were
/// one action for an ordinary call, where nothing can be waited for once the window is gone.
/// </para>
/// </remarks>
public sealed class BackgroundTask
{
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ToolCallOutcome> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Func<BackgroundTask, Task>? _stop;
    private readonly CancellationTokenSource _abandoned = new();
    private ElicitationPrompter? _prompter;

    internal BackgroundTask(
        string id, ToolInfo tool, ServerConnection connection, DateTimeOffset started,
        ElicitationPrompter? prompter, Func<BackgroundTask, Task>? stop)
    {
        Id = id;
        Tool = tool;
        Connection = connection;
        Started = started;
        _prompter = prompter;
        _stop = stop;
    }

    /// <summary>A task for a test or a list to describe, polled by nobody.</summary>
    public static BackgroundTask Detached(string id, ToolInfo tool, ServerConnection connection, DateTimeOffset started)
        => new(id, tool, connection, started, null, null);

    /// <summary>The server's identifier for it. Never spoken: it is a server's token, not a name.</summary>
    public string Id { get; }

    public ToolInfo Tool { get; }

    /// <summary>The server running it, which its result's links are read through.</summary>
    public ServerConnection Connection { get; }

    /// <summary>When humcp asked for it, in local time.</summary>
    public DateTimeOffset Started { get; }

    /// <summary>When it reached an end, or null while it has not.</summary>
    public DateTimeOffset? Ended { get; private set; }

    public BackgroundTaskState State { get; private set; } = BackgroundTaskState.Working;

    /// <summary>The server's own line about where it is, sanitised; empty when it has said none.</summary>
    public string StatusMessage { get; private set; } = "";

    /// <summary>The answer, once there is one. Null while it runs, and for a task that was stopped.</summary>
    public ToolCallOutcome? Outcome { get; private set; }

    /// <summary>Whether <see cref="StopAsync"/> has been asked, so a list can say "stopping".</summary>
    public bool StopRequested { get; private set; }

    public bool IsFinished => State is not (BackgroundTaskState.Working or BackgroundTaskState.NeedsAnswer);

    /// <summary>
    /// The answer, when there is one. Faults with <see cref="OperationCanceledException"/> for a task
    /// the server stopped, as an ordinary call that was stopped does.
    /// </summary>
    public Task<ToolCallOutcome> Completion => _completion.Task;

    /// <summary>
    /// Anything a list shows about it changed. Raised on whatever thread found out, which is never
    /// the UI's.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Where a question the server asks during the task is shown. The window that started the task
    /// at first; when that window closes, whoever takes the task over sets another, because a
    /// question sent to a closed window would be declined without anyone having seen it.
    /// </summary>
    public ElicitationPrompter? Prompter
    {
        get { lock (_gate) return _prompter; }
        set { lock (_gate) _prompter = value; }
    }

    /// <summary>
    /// Asks the server to stop it (<c>tasks/cancel</c>). Cooperative, as the extension says: the
    /// server acknowledges and decides, and the task may still finish with a result if the work was
    /// done before the request arrived. The state changes when the server says it has.
    /// </summary>
    public async Task StopAsync()
    {
        if (IsFinished || StopRequested || _stop is null) return;

        StopRequested = true;
        RaiseChanged();
        _abandoned.Cancel();

        await _stop(this).ConfigureAwait(false);
    }

    /// <summary>
    /// Cancelled when the task is stopped or ends, so a question it asked does not go on waiting for
    /// an answer the server no longer wants.
    /// </summary>
    internal CancellationToken Abandoned => _abandoned.Token;

    /// <summary>A poll found it still going, perhaps with a new line to say about itself.</summary>
    internal void Report(BackgroundTaskState state, string? statusMessage)
    {
        bool changed;

        lock (_gate)
        {
            if (IsFinished) return;

            var message = TextSanitizer.Clean(statusMessage, TextSanitizer.HelpLength);
            changed = state != State || message != StatusMessage;
            State = state;
            StatusMessage = message;
        }

        if (changed) RaiseChanged();
    }

    /// <summary>It reached an end. Only the first end counts: a late poll cannot overwrite a result.</summary>
    internal void Finish(BackgroundTaskState state, ToolCallOutcome? outcome, string? statusMessage = null)
    {
        lock (_gate)
        {
            if (IsFinished) return;

            State = state;
            Outcome = outcome;
            Ended = DateTimeOffset.Now;
            if (statusMessage is not null) StatusMessage = TextSanitizer.Clean(statusMessage, TextSanitizer.HelpLength);
        }

        RaiseChanged();
        _abandoned.Cancel();

        if (state == BackgroundTaskState.Stopped) _completion.TrySetCanceled();
        else _completion.TrySetResult(outcome ?? ToolCallOutcome.Failure(StatusMessage));
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// The task as one line in a list: what, where, how it stands, and when. Times are clock times
    /// rather than "two minutes ago", which would be wrong a minute later in a list nobody refreshed.
    /// </summary>
    public string Describe()
    {
        var tool = TextSanitizer.Clean(Tool.DisplayName, TextSanitizer.NameLength);
        var server = TextSanitizer.Clean(Connection.Descriptor.DisplayName, TextSanitizer.NameLength);

        var standing = State switch
        {
            BackgroundTaskState.Working when StopRequested => "stopping",
            BackgroundTaskState.Working => "running",
            BackgroundTaskState.NeedsAnswer => "waiting for your answer",
            BackgroundTaskState.Completed when Outcome is { IsError: true } => "finished with an error",
            BackgroundTaskState.Completed => "finished",
            BackgroundTaskState.Stopped => "stopped",
            BackgroundTaskState.Failed => "failed",
            _ => "lost",
        };

        var when = Ended is { } ended
            ? $"started {Clock(Started)}, ended {Clock(ended)}"
            : $"started {Clock(Started)}";

        var line = $"{tool} on {server}, {standing}, {when}.";
        return StatusMessage.Length > 0 ? $"{line} {StatusMessage}" : line;
    }

    /// <summary>
    /// What is said when it ends and nobody was waiting for it — the window that started it closed,
    /// or the main window handed it over — so the user hears it wherever they are.
    /// </summary>
    public string Ending()
    {
        var tool = TextSanitizer.Clean(Tool.DisplayName, TextSanitizer.NameLength);

        return State switch
        {
            BackgroundTaskState.Completed when Outcome is { IsError: true } =>
                $"{tool} finished with an error. Control plus T shows it.",
            BackgroundTaskState.Completed => $"{tool} has finished. Control plus T shows its result.",
            BackgroundTaskState.Stopped => $"{tool} was stopped.",
            BackgroundTaskState.Failed or BackgroundTaskState.Lost =>
                StatusMessage.Length > 0 ? $"{tool} did not finish. {StatusMessage}" : $"{tool} did not finish.",
            _ => "",
        };
    }

    private static string Clock(DateTimeOffset time)
        => time.ToLocalTime().ToString("T", CultureInfo.CurrentCulture);
}
