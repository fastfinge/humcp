namespace Humcp.Core;

/// <summary>
/// How big humcp's windows and panes are, decided from the screen and from what is in them rather
/// than by whoever is at the keyboard (ROADMAP §10, Phase 6).
/// </summary>
/// <remarks>
/// A screen reader reads all of a pane whatever its width, so the person most likely to be using
/// humcp is the one person who cannot judge whether the servers pane is cutting tool names in half
/// or leaving the result squeezed into a strip — and a sighted colleague looking over their shoulder
/// sees whatever the defaults chose. So the defaults choose from the screen and the content, and a
/// width somebody set on purpose — by dragging the divider, or in the pane-width form — is kept
/// until they ask for fitting back. Pure, and in device-independent pixels throughout, so the rules
/// are tested without a window.
/// </remarks>
public static class PaneLayout
{
    /// <summary>The share of the work area a window takes when nothing says otherwise.</summary>
    public const double MainWindowShare = 0.85;

    /// <summary>Other windows are never bigger than this share of the work area.</summary>
    public const double OtherWindowShare = 0.9;

    /// <summary>The servers pane never takes more than this share of the window when fitted.</summary>
    public const double MaxTreeShare = 0.5;

    /// <summary>Room for the tree's own scroll bar and border beside the widest item.</summary>
    public const double TreeChrome = 32;

    /// <summary>
    /// The main window's starting size: most of the work area, never smaller than the window's own
    /// minimum unless the screen itself is smaller, and never bigger than the screen.
    /// </summary>
    public static (double Width, double Height) MainWindow(double workWidth, double workHeight, double minWidth, double minHeight)
        => (Fit(workWidth * MainWindowShare, minWidth, workWidth),
            Fit(workHeight * MainWindowShare, minHeight, workHeight));

    /// <summary>
    /// Another window's size: what it asked for, shrunk to fit the screen if it would not. A tool
    /// form is 800 high, and a laptop's work area is often less than that, which would put the Run
    /// button below the bottom of the screen.
    /// </summary>
    public static (double Width, double Height) OtherWindow(double width, double height, double workWidth, double workHeight)
        => (Math.Min(width, workWidth * OtherWindowShare), Math.Min(height, workHeight * OtherWindowShare));

    /// <summary>
    /// The servers pane fitted to its widest item: wide enough to show it whole if the window allows,
    /// never narrower than <paramref name="treeMin"/>, never more than half the window, and never so
    /// wide that the result pane drops below <paramref name="resultMin"/>.
    /// </summary>
    /// <param name="contentWidth">What the tree needs to show its widest item without cutting it off.</param>
    /// <param name="available">The width the two panes share.</param>
    public static double FittedTree(double contentWidth, double available, double treeMin, double resultMin)
    {
        var wanted = contentWidth + TreeChrome;
        var ceiling = Math.Min(available * MaxTreeShare, available - resultMin);
        return Fit(wanted, treeMin, Math.Max(treeMin, ceiling));
    }

    /// <summary>A width set as a percentage, held to the same limits a fitted one is.</summary>
    public static double FromPercent(int percent, double available, double treeMin, double resultMin)
        => Fit(available * percent / 100.0, treeMin, Math.Max(treeMin, available - resultMin));

    /// <summary><paramref name="value"/> held between the bounds, where the upper one wins if they cross.</summary>
    private static double Fit(double value, double lower, double upper)
        => Math.Min(Math.Max(value, lower), upper);
}
