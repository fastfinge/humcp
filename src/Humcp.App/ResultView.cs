using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.App;

/// <summary>
/// The result of one call, rendered (ROADMAP §10, Phase 4). One control, used by the main window
/// for a call made from the tree and by a form window for a call made from a form.
/// </summary>
/// <remarks>
/// <para>
/// No part of this file knows anything about any particular tool or any particular server, which is
/// the same claim the form makes on the way in (§1). A server answers with parts;
/// <see cref="ResultSections"/> turns them into things a window can hold;
/// <see cref="ResultTemplateSelector"/> turns those into controls; and what comes out is navigable
/// by keyboard and readable by a screen reader without a line of code about that server.
/// </para>
/// <para>
/// The parts live in a <see cref="FormItemsControl"/>, deliberately — a result is a generated list
/// of controls in exactly the sense a form is, and the three defaults that make WPF's
/// <c>ItemsControl</c> unusable with a screen reader (§7.12, §7.13, §7.6) are wrong here for the
/// same reasons.
/// </para>
/// </remarks>
public sealed class ResultView : ContentControl
{
    private readonly FormItemsControl _parts = new()
    {
        Margin = new Thickness(0, 0, 12, 0),
    };

    private ToolCallOutcome? _outcome;

    public ResultView()
    {
        // Not a tab stop: a container is not a control (§7.13, §7.14).
        Focusable = false;
        _parts.ItemTemplateSelector = new ResultTemplateSelector();

        var scroller = new ScrollViewer
        {
            Content = _parts,
            Focusable = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        Content = scroller;

        Bind(ResultCommands.Save, OnSave);
        Bind(ResultCommands.Open, OnOpen);
        Bind(ResultCommands.Follow, OnFollow);
        Bind(ResultCommands.Copy, OnCopy);
        Bind(ResultCommands.Expand, OnExpand);
    }

    /// <summary>The window this view is in, for the windows its parts open.</summary>
    public Window? Owner { get; set; }

    public WindowManager? Windows { get; set; }

    /// <summary>
    /// Where this view says what it just did. The window owns the live region (§7's core rule), so
    /// saving a file or copying a link is announced through the same channel as everything else and
    /// never through prism.
    /// </summary>
    public Action<string>? Announce { get; set; }

    /// <summary>What is on screen now, for the details window.</summary>
    public ToolCallOutcome? Outcome => _outcome;

    /// <summary>Shows one result. Replaces whatever was there.</summary>
    public void Show(ToolInfo? tool, ToolCallOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        _outcome = outcome;
        _parts.ItemsSource = ResultSections.Build(tool, outcome);
    }

    /// <summary>Shows a single line of text — "no tool has been run yet", and the like.</summary>
    public void ShowMessage(string heading, string text)
    {
        _outcome = null;
        // Help text even here: a box with neither help nor a label is one a reader announces as a bare
        // name, and this is the first thing F6 lands on in a window that has run nothing yet.
        _parts.ItemsSource = new[] { new TextSection(heading, text) { HelpText = "Results appear here when a tool runs." } };
    }

    /// <summary>
    /// Moves the caret to the first control of the first part, for <kbd>F6</kbd>. False when there
    /// is nothing to move it to, so a pane cycle can pass over an empty result rather than
    /// stopping on it.
    /// </summary>
    public bool FocusFirst()
    {
        _parts.UpdateLayout();
        return _parts.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
    }

    private void Bind(RoutedUICommand command, ExecutedRoutedEventHandler executed) =>
        CommandBindings.Add(new CommandBinding(command, executed));

    // ------------------------------------------------------------------ what the buttons do

    private void OnSave(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not MediaSection media || Owner is null) return;

        Say(Launcher.Save(Owner, media.Block));
    }

    private void OnOpen(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not MediaSection media) return;

        Say(Launcher.Open(media.Block));
    }

    private void OnFollow(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not LinkSection link) return;

        Say(Launcher.Follow(link.Uri));
    }

    private void OnCopy(object sender, ExecutedRoutedEventArgs e)
    {
        switch (e.Parameter)
        {
            case LinkSection link:
                Say(Launcher.Copy("the link", link.Uri));
                break;

            case TextSection text:
                Say(Launcher.Copy(text.Heading, text.Text));
                break;

            case TypeAheadTreeView { SelectedItem: ResultNode node }:
                Say(Launcher.Copy(node.Label, node.Shape == ResultShape.Value ? node.Text : node.Json()));
                break;

            case ResultDataGrid grid when CurrentCell(grid) is { } cell:
                Say(Launcher.Copy("the cell", cell.Detail is { } detail ? detail.Json() : cell.Text));
                break;

            default:
                break;
        }
    }

    /// <summary>
    /// Opens whatever the caret is on in a window of its own: a table as a grid, a long string as
    /// text, anything else as the JSON it is.
    /// </summary>
    private void OnExpand(object sender, ExecutedRoutedEventArgs e)
    {
        switch (e.Parameter)
        {
            case TypeAheadTreeView { SelectedItem: ResultNode node }:
                Open(node);
                break;

            case ResultDataGrid grid when CurrentCell(grid)?.Detail is { } detail:
                Open(detail);
                break;

            case ResultDataGrid:
                Say("That cell holds one value, which is already on screen.");
                break;

            default:
                break;
        }
    }

    private void Open(ResultNode node)
    {
        if (Owner is null || Windows is null) return;

        if (ResultWindows.Open(Owner, node, Windows) is { } message) Say(message);
    }

    private static ResultCell? CurrentCell(ResultDataGrid grid)
    {
        if (grid.CurrentCell is not { IsValid: true } current) return null;
        if (current.Item is not ResultRow row) return null;

        var index = current.Column?.DisplayIndex ?? -1;
        return index >= 0 && index < row.Cells.Count ? row.Cells[index] : null;
    }

    private void Say(string message)
    {
        if (message.Length > 0) Announce?.Invoke(message);
    }

    /// <summary>
    /// A group, and one that exists at all. A bare <see cref="ContentControl"/> creates no
    /// automation peer, so the view itself is absent from the UI Automation tree — which is
    /// invisible in use, since its parts are still there, and fatal to any test that wants to say
    /// "the result pane of <em>this</em> window" (§9: an owned window is a descendant of its owner,
    /// so a lookup has to be able to scope itself).
    /// </summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ResultViewPeer(this);
}

internal sealed class ResultViewPeer(ResultView owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetClassNameCore() => "ResultView";

    /// <summary>Empty on purpose: every part inside carries its own name (§7.12).</summary>
    protected override string GetNameCore() => string.Empty;
}
