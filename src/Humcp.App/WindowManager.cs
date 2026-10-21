using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace Humcp.App;

/// <summary>
/// Every window humcp has open, and the two things that turn a pile of windows into an application
/// you can navigate: a Window menu on each of them, and <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd>
/// (ROADMAP §8, and §7's keyboard map).
/// </summary>
/// <remarks>
/// <para>
/// The model is the one old multi-document Windows applications used, and it is deliberate. humcp
/// generates a window per tool, and a session with a dozen servers can have a great many open at
/// once; putting all of them in <kbd>Alt</kbd>+<kbd>Tab</kbd> would bury every other application on
/// the machine under them. So every window but the main one is kept out of the task bar and out of
/// <kbd>Alt</kbd>+<kbd>Tab</kbd>, and the way between them is inside humcp, where it can be a named
/// list with numbers on it rather than a strip of icons.
/// </para>
/// <para>
/// Added in Phase 3 rather than Phase 6, after the first manual pass said the form windows "read
/// like dialogs rather than windows" (phase3-notes 4.3). They were: no menu bar, no way to get from
/// one to another, and a Close button that did nothing.
/// </para>
/// </remarks>
public sealed class WindowManager
{
    /// <summary>Switches to the nth open window. Parameter: a zero-based index.</summary>
    public static readonly RoutedUICommand Switch =
        new("", nameof(Switch), typeof(WindowManager));

    /// <summary>Closes the window it is invoked on. Not bound on the main window.</summary>
    /// <remarks>
    /// Empty text, like every other command here: WPF announces a bound command's <c>Text</c> as a
    /// button's keyboard shortcut when none is set (§7.27).
    /// </remarks>
    public static readonly RoutedUICommand CloseWindow =
        new("", nameof(CloseWindow), typeof(WindowManager));

    /// <summary>Says the last status line again, through prism. <kbd>Ctrl+R</kbd>, in every window.</summary>
    /// <remarks>
    /// Not <kbd>Ctrl+Shift+R</kbd>, the first choice: on the development machine another program holds
    /// it as a system-wide hotkey, and humcp never received it at all (phase9-notes 5.2).
    /// </remarks>
    public static readonly RoutedUICommand RepeatAnnouncement =
        new("", nameof(RepeatAnnouncement), typeof(WindowManager));

    private readonly ObservableCollection<Window> _windows = [];

    /// <summary>
    /// Speaks for <see cref="RepeatAnnouncement"/>: the screen reader directly, through prism. Set by
    /// the main window, which owns the announcer.
    /// </summary>
    public Humcp.Speech.IAnnouncer? Announcer { get; set; }

    /// <summary>
    /// Where a repeat goes when prism has no screen reader to speak through: back into a live region,
    /// which is read by whatever reader is there, prism or not.
    /// </summary>
    public Action<string>? Unspoken { get; set; }

    /// <summary>How many windows get a <kbd>Ctrl</kbd>+number shortcut. The rest are in the menu.</summary>
    public const int Shortcuts = 9;

    public IReadOnlyList<Window> Windows => _windows;

    /// <summary>The main window: tracked, and the only one that keeps its place in the task bar.</summary>
    public void TrackMain(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _windows.Add(window);
    }

    /// <summary>
    /// A window humcp opened for itself. Call before <c>Show</c>: it leaves the task bar, which is
    /// what keeps it out of <kbd>Alt</kbd>+<kbd>Tab</kbd> as well.
    /// </summary>
    public void Track(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        window.ShowInTaskbar = false;

        // Never bigger than the screen it opens on (PaneLayout.OtherWindow): a tool form asks for
        // 800 high, and a laptop's work area is often less, which would put Run below the bottom.
        if (!double.IsNaN(window.Width) && !double.IsNaN(window.Height))
        {
            var area = SystemParameters.WorkArea;
            (window.Width, window.Height) = Humcp.Core.PaneLayout.OtherWindow(window.Width, window.Height, area.Width, area.Height);
        }

        _windows.Add(window);
        window.Closed += (_, _) => _windows.Remove(window);

        // Handed back before the window goes, not after. Left to Windows, closing the window in
        // front activates whatever was in front before humcp — in the 1.1 NVDA pass, a terminal —
        // so a user who saved a server's settings heard nothing, and was in another application.
        // While this window is still active, humcp may activate its own owner; once it has gone,
        // the foreground is no longer humcp's to give.
        window.Closing += (_, e) =>
        {
            if (!e.Cancel && window.IsActive && window.Owner is { } owner) owner.Activate();
        };
    }

    /// <summary>
    /// Wires a <c>Window</c> menu declared in XAML, and gives the window its switching shortcuts.
    /// </summary>
    /// <param name="closable">
    /// False for the main window, where <kbd>Escape</kbd> must not close the application.
    /// </param>
    public void Attach(Window window, MenuItem windowMenu, bool closable)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(windowMenu);

        // Rebuilt each time it is opened rather than kept in step: the list is short, the cost is
        // nothing, and a menu that is built once is a menu that is wrong after the second window.
        windowMenu.SubmenuOpened += (_, _) => Fill(windowMenu, window);
        Fill(windowMenu, window);

        window.CommandBindings.Add(new CommandBinding(Switch, (_, e) =>
        {
            if (e.Parameter is int index && index >= 0 && index < _windows.Count)
                Activate(_windows[index]);
        }));

        for (var i = 0; i < Shortcuts; i++)
        {
            window.InputBindings.Add(new KeyBinding(Switch, new KeyGesture((Key)((int)Key.D1 + i), ModifierKeys.Control))
            {
                CommandParameter = i,
            });
        }

        window.CommandBindings.Add(new CommandBinding(RepeatAnnouncement, (_, _) => Repeat()));
        window.InputBindings.Add(new KeyBinding(RepeatAnnouncement, new KeyGesture(Key.R, ModifierKeys.Control)));

        if (!closable) return;

        window.CommandBindings.Add(new CommandBinding(CloseWindow, (_, _) => window.Close()));
        window.InputBindings.Add(new KeyBinding(CloseWindow, new KeyGesture(Key.Escape)));
        window.InputBindings.Add(new KeyBinding(CloseWindow, new KeyGesture(Key.W, ModifierKeys.Control)));
    }

    /// <summary>
    /// A menu bar for a window built in code, carrying <paramref name="leading"/> menus and then
    /// the Window menu.
    /// </summary>
    /// <remarks>
    /// Not a tab stop, in three ways, because WPF makes one by default: a menu bar is reached with
    /// <kbd>Alt</kbd> and arrowed between, never tabbed to (§7.14).
    /// </remarks>
    public Menu CreateMenuBar(Window window, bool closable, params MenuItem[] leading)
    {
        ArgumentNullException.ThrowIfNull(window);

        var bar = new Menu { Focusable = false, IsTabStop = false };
        KeyboardNavigation.SetTabNavigation(bar, KeyboardNavigationMode.None);
        AutomationProperties.SetName(bar, "Main menu");

        foreach (var item in leading ?? []) bar.Items.Add(item);

        var windowMenu = new MenuItem { Header = "_Window" };
        bar.Items.Add(windowMenu);

        Attach(window, windowMenu, closable);
        return bar;
    }

    /// <summary>
    /// Says the last status line again — through prism, because that is the one channel that is heard
    /// at the moment the user asks, whatever has the focus and whatever the reader was doing. A live
    /// region is spoken once, when it changes, and only in the foreground window; this is for the time
    /// that was missed (phase9-notes 5.1). Interrupting: the user asked for it now.
    /// </summary>
    public void Repeat()
    {
        var text = LiveRegion.Last ?? "Nothing has been announced yet.";

        if (Announcer is { IsAvailable: true } announcer) announcer.Output(text, interrupt: true);
        else Unspoken?.Invoke(text);
    }

    /// <summary>Brings a window to the front, un-minimising it if it needs it.</summary>
    public static void Activate(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;

        window.Show();
        window.Activate();
        window.Focus();
    }

    private void Fill(MenuItem menu, Window current)
    {
        menu.Items.Clear();

        for (var index = 0; index < _windows.Count; index++)
        {
            var window = _windows[index];
            var title = window.Title.Length > 0 ? window.Title : "Untitled";

            var item = new MenuItem
            {
                // The number is the access key as well as the label, so the menu and
                // Ctrl+number agree about which window is which.
                Header = index < Shortcuts ? $"_{index + 1} {Escape(title)}" : $"{index + 1} {Escape(title)}",

                // Checkable as well as checked, because that is what puts the state where a screen
                // reader can reach it. With IsChecked alone WPF draws the mark and exposes no toggle
                // state at all, so the window you are actually in is the one nobody is told about
                // (phase3-notes 4.2). Clicking still switches windows rather than toggling: the
                // handler below runs and the menu closes.
                IsCheckable = true,
                IsChecked = ReferenceEquals(window, current),

                InputGestureText = index < Shortcuts ? $"Ctrl+{index + 1}" : "",
            };

            AutomationProperties.SetName(item, $"{index + 1} {title}");

            var target = window;
            item.Click += (_, _) => Activate(target);

            menu.Items.Add(item);
        }
    }

    /// <summary>A window title that happens to contain an underscore must not eat the letter after it.</summary>
    private static string Escape(string title) => title.Replace("_", "__", StringComparison.Ordinal);
}
