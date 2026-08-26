using System.Diagnostics;
using System.IO;
using System.Windows;
using Humcp.Core;
using Microsoft.Win32;

namespace Humcp.App;

/// <summary>
/// The one place in humcp that hands anything to the shell, and the enforcement point for the two
/// rules that make that safe (<see cref="MediaTypes"/> and <see cref="LinkPolicy"/>).
/// </summary>
/// <remarks>
/// Everything here acts on bytes and strings a third-party server chose (ROADMAP §4, pitfall 5).
/// The policies live in <c>Humcp.Core</c> so they can be tested without a desktop; this file is
/// only the doing, and it asks before it does rather than deciding for itself. Every method answers
/// with the sentence to announce — an action whose outcome is not said out loud is an action a user
/// working by ear cannot check.
/// </remarks>
internal static class Launcher
{
    /// <summary>Writes a part to a file the user names. The extension is ours, never the server's.</summary>
    public static string Save(Window owner, MediaResultBlock block)
    {
        var dialog = new SaveFileDialog
        {
            FileName = block.SuggestedFileName,
            DefaultExt = MediaTypes.ExtensionFor(block.MimeType),
            Filter = $"{block.Description} (*{MediaTypes.ExtensionFor(block.MimeType)})" +
                     $"|*{MediaTypes.ExtensionFor(block.MimeType)}|All files (*.*)|*.*",
            Title = $"Save {block.Description}",
            OverwritePrompt = true,
        };

        if (dialog.ShowDialog(owner) != true) return "Not saved.";

        try
        {
            File.WriteAllBytes(dialog.FileName, block.Data.Span);
            return $"Saved {block.Description} to {dialog.FileName}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"Could not save it. {ex.Message}";
        }
    }

    /// <summary>
    /// Opens a part with whatever program Windows associates with the type <em>humcp</em> gave it.
    /// </summary>
    /// <remarks>
    /// The file is written into a folder of ours with an extension from
    /// <see cref="MediaTypes.ExtensionFor"/>, so what the shell is asked to open is a name this
    /// application chose. A media type humcp does not recognise never reaches here — the button is
    /// not offered — because "open" on an unrecognised type is "run whatever this turns out to be".
    /// </remarks>
    public static string Open(MediaResultBlock block)
    {
        if (!block.CanOpen)
            return $"humcp does not know what {block.MimeType} is, so it can be saved but not opened.";

        try
        {
            var folder = Path.Combine(Path.GetTempPath(), "humcp");
            Directory.CreateDirectory(folder);

            var path = Path.Combine(folder, $"{Guid.NewGuid():N}{MediaTypes.ExtensionFor(block.MimeType)}");
            File.WriteAllBytes(path, block.Data.Span);

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
            return $"Opened {block.Description}.";
        }
        catch (Exception ex)
        {
            return $"Could not open it. {ex.Message}";
        }
    }

    /// <summary>Follows a link as far as <see cref="LinkPolicy"/> permits, and says what it did.</summary>
    public static string Follow(string uri)
    {
        switch (LinkPolicy.Action(uri))
        {
            case LinkAction.Browse:
                try
                {
                    Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
                    return $"Opened {uri} in your browser.";
                }
                catch (Exception ex)
                {
                    return $"Could not open it. {ex.Message}";
                }

            case LinkAction.Reveal:
                try
                {
                    var path = new Uri(uri).LocalPath;

                    // One argument, built here and passed through ArgumentList so that .NET does the
                    // quoting: a path is a string the server chose, and a path with a quote in it
                    // must not become a second argument to Explorer.
                    Process.Start(new ProcessStartInfo("explorer.exe")
                    {
                        ArgumentList = { "/select," + path },
                        UseShellExecute = true,
                    })?.Dispose();

                    return $"Showing {path} in a folder window.";
                }
                catch (Exception ex)
                {
                    return $"Could not show it. {ex.Message}";
                }

            default:
                return LinkPolicy.Describe(uri);
        }
    }

    /// <summary>Puts text on the clipboard, which fails often enough to be worth saying so.</summary>
    public static string Copy(string what, string text)
    {
        if (text.Length == 0) return $"There is nothing in {what} to copy.";

        try
        {
            Clipboard.SetText(text);
            return $"Copied {what}. {text.Length:N0} characters.";
        }
        catch (Exception ex)
        {
            // The clipboard belongs to whatever last opened it, and that is not always us.
            return $"Could not copy it. {ex.Message}";
        }
    }
}
