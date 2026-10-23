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
    private ToolInfo? _tool;

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
        Bind(ResultCommands.Describe, (_, e) =>
        {
            if (e.Parameter is MediaSection media) _ = DescribeAsync(media, asked: true);
        });
        Bind(ResultCommands.Summarise, (_, e) =>
        {
            if (e.Parameter is SummarySection summary) _ = SummariseAsync(summary);
        });

        // Ctrl+D anywhere in the result: the image the caret is on, or the only one there is. Ctrl+M:
        // a summary of the whole result.
        PreviewKeyDown += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;

            if (e.Key == Key.D)
            {
                e.Handled = true;
                if (ImageAtCaret() is { } media) _ = DescribeAsync(media, asked: true);
                else Say("There is no image here to describe.");
            }
            else if (e.Key == Key.M)
            {
                e.Handled = true;
                Summarise();
            }
        };
    }

    /// <summary>Stops any description in progress — the window is closing, and its answer would be said to nobody.</summary>
    public void StopDescribing() => _describing?.Cancel();

    /// <summary>The local model, for alt text (Phase 10). Null in a view that has none to offer.</summary>
    internal LocalModelFeatures? Describer { get; set; }

    /// <summary>Stopped when the result is replaced: a description of a picture no longer on screen is said to nobody.</summary>
    private CancellationTokenSource? _describing;

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
        _tool = tool;
        var sections = ResultSections.Build(tool, outcome);

        // The offer of a summary, at the end, for a result long enough to want one (Phase 11).
        if (Describer is { } features && ResultSummary.IsWorthSummarising(outcome))
            sections = [.. sections, new SummarySection(features.IsDownloaded ? ResultSummary.Offer : ResultSummary.NotDownloaded)];

        _parts.ItemsSource = sections;

        StartDescribing(sections);
    }

    /// <summary>Shows a single line of text — "no tool has been run yet", and the like.</summary>
    /// <param name="help">What the box is for, said on focus. A prompt's window says "fetched", not "runs" (Phase 12).</param>
    public void ShowMessage(string heading, string text, string help = "Results appear here when a tool runs.")
    {
        _outcome = null;
        _describing?.Cancel();
        // Help text even here: a box with neither help nor a label is one a reader announces as a bare
        // name, and this is the first thing F6 lands on in a window that has run nothing yet.
        _parts.ItemsSource = new[] { new TextSection(heading, text) { HelpText = help } };
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

    // ------------------------------------------------------------------ alt text (Phase 10)

    /// <summary>
    /// Says what each image's box should say now, and starts describing them if the setting says to.
    /// One at a time, in order: the model answers one request at a time anyway, and the first picture
    /// is the one most likely to be read first.
    /// </summary>
    private void StartDescribing(IEnumerable<object> sections)
    {
        _describing?.Cancel();
        _describing = new CancellationTokenSource();

        var images = sections.OfType<MediaSection>().Where(m => m.CanDescribe).ToList();
        if (images.Count == 0 || Describer is not { } describer) return;

        foreach (var image in images)
            image.AltText = describer.DescribesAutomatically ? ImageDescription.Describing
                : describer.IsDownloaded ? ImageDescription.Off
                : ImageDescription.NotDownloaded;

        if (describer.DescribesAutomatically) _ = DescribeAllAsync(images, _describing.Token);
    }

    private async Task DescribeAllAsync(IReadOnlyList<MediaSection> images, CancellationToken cancellation)
    {
        foreach (var image in images)
        {
            if (cancellation.IsCancellationRequested) return;
            await DescribeAsync(image, asked: false, cancellation);
        }
    }

    /// <param name="asked">
    /// True for Describe and Ctrl+D, which answer even when there is nothing to do; false for the
    /// automatic pass, which says only that a description is ready — the result's own announcement
    /// has just been made, and the box says "describing" for anyone who goes to look.
    /// </param>
    private async Task DescribeAsync(MediaSection image, bool asked, CancellationToken cancellation = default)
    {
        if (Describer is not { } describer) return;

        if (!image.CanDescribe)
        {
            if (asked) Say("That part is not a picture humcp can show, so the model cannot be asked about it either.");
            return;
        }

        if (image.IsDescribed)
        {
            if (asked) Say("It is already described: the description is in the box under the picture.");
            return;
        }

        if (image.IsDescribing)
        {
            if (asked) Say("It is being described now.");
            return;
        }

        if (!describer.IsDownloaded)
        {
            if (asked) Say(ImageDescription.NotDownloaded);
            return;
        }

        if (cancellation == default) cancellation = _describing?.Token ?? CancellationToken.None;

        image.IsDescribing = true;
        image.AltText = ImageDescription.Describing;
        if (asked) Say("Describing the image. The description will be announced when it is ready.");

        try
        {
            var text = await describer.DescribeAsync(image.Block.Data, cancellation);

            image.IsDescribed = true;
            image.AltText = ImageDescription.Generated(text);
            Say(ImageDescription.Ready(image.Block.Phrase));
        }
        catch (OperationCanceledException)
        {
            // The result was replaced. Nobody is waiting for this one.
        }
        catch (Humcp.Inference.LocalModelException ex)
        {
            image.AltText = ImageDescription.Failed(ex.Message);
            Say(image.AltText);
        }
        finally
        {
            image.IsDescribing = false;
        }
    }

    /// <summary>
    /// <kbd>Ctrl+M</kbd>: the summary of whatever result is on screen. Public because the window binds
    /// it too — from a form's fields or the main window's tree, not only from inside the result
    /// (found by ear, phase11-notes).
    /// </summary>
    public void Summarise()
    {
        if (Describer is null || _outcome is null) return;

        if ((_parts.ItemsSource as IEnumerable<object> ?? []).OfType<SummarySection>().FirstOrDefault() is { } summary)
            _ = SummariseAsync(summary);
        else
            Say("This result is short enough to read whole, so there is no summary to make.");
    }

    /// <summary>
    /// Asks the model for a summary of the result on screen. On request only (Phase 11): a long
    /// result is minutes of processor, and nothing runs that nobody asked for. Said when it is ready;
    /// the caret stays where it is.
    /// </summary>
    private async Task SummariseAsync(SummarySection summary)
    {
        if (Describer is not { } features || _outcome is not { } outcome) return;

        if (summary.IsSummarised)
        {
            Say("It is already summarised: the summary is the last part of the result.");
            return;
        }

        if (summary.IsSummarising)
        {
            Say("It is being summarised now.");
            return;
        }

        if (!features.IsDownloaded)
        {
            Say(ResultSummary.NotDownloaded);
            return;
        }

        summary.IsSummarising = true;
        summary.Text = ResultSummary.Summarising;
        Say("Summarising the result. The summary will be announced when it is ready.");

        try
        {
            var text = await features.SummariseAsync(_tool?.DisplayName ?? "a tool", outcome, _describing?.Token ?? CancellationToken.None);

            summary.IsSummarised = true;
            summary.Text = text;
            Say(ResultSummary.Ready("as the last part of the result"));
        }
        catch (OperationCanceledException)
        {
            // The result was replaced.
        }
        catch (Humcp.Inference.LocalModelException ex)
        {
            summary.Text = ResultSummary.Failed(ex.Message);
            Say(summary.Text);
        }
        finally
        {
            summary.IsSummarising = false;
        }
    }

    /// <summary>The image the caret is in — its box or its buttons — or, with the caret elsewhere, the only image in the result.</summary>
    private MediaSection? ImageAtCaret()
    {
        if ((Keyboard.FocusedElement as FrameworkElement)?.DataContext is MediaSection { CanDescribe: true } here) return here;

        var images = (_parts.ItemsSource as IEnumerable<object> ?? []).OfType<MediaSection>().Where(m => m.CanDescribe).ToList();
        return images.Count == 1 ? images[0] : null;
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
