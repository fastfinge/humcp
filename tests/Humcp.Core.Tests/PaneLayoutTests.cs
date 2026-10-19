namespace Humcp.Core.Tests;

public class PaneLayoutTests
{
    [Fact]
    public void The_main_window_takes_most_of_the_screen()
    {
        var (width, height) = PaneLayout.MainWindow(1920, 1040, 800, 560);

        Assert.Equal(1632, width);
        Assert.Equal(884, height);
    }

    [Fact]
    public void A_small_screen_is_filled_rather_than_overflowed()
    {
        // A 1024 by 768 screen with a task bar: the minimum would not fit, so the screen wins.
        var (width, height) = PaneLayout.MainWindow(1024, 728, 1100, 800);

        Assert.Equal(1024, width);
        Assert.Equal(728, height);
    }

    [Fact]
    public void A_window_taller_than_the_screen_is_shrunk_to_fit_it()
    {
        var (width, height) = PaneLayout.OtherWindow(820, 800, 1366, 728);

        Assert.Equal(820, width);
        Assert.Equal(728 * PaneLayout.OtherWindowShare, height, 3);
    }

    [Fact]
    public void A_window_that_fits_keeps_the_size_it_asked_for()
        => Assert.Equal((660.0, 600.0), PaneLayout.OtherWindow(660, 600, 1920, 1040));

    [Fact]
    public void The_tree_is_as_wide_as_its_widest_item()
        => Assert.Equal(300 + PaneLayout.TreeChrome, PaneLayout.FittedTree(300, 1400, 260, 320));

    [Fact]
    public void The_tree_never_takes_more_than_half_the_window()
        => Assert.Equal(700, PaneLayout.FittedTree(2000, 1400, 260, 320));

    [Fact]
    public void A_short_tree_still_gets_its_minimum()
        => Assert.Equal(260, PaneLayout.FittedTree(40, 1400, 260, 320));

    [Fact]
    public void A_narrow_window_keeps_the_result_pane_usable()
        // Half of 600 is 300, but the result pane needs 320 of the 600: the tree gets 280.
        => Assert.Equal(280, PaneLayout.FittedTree(2000, 600, 260, 320));

    [Fact]
    public void A_percentage_is_held_to_the_same_limits()
    {
        Assert.Equal(840, PaneLayout.FromPercent(60, 1400, 260, 320));
        Assert.Equal(260, PaneLayout.FromPercent(5, 1400, 260, 320));
        Assert.Equal(1080, PaneLayout.FromPercent(95, 1400, 260, 320));
    }
}
