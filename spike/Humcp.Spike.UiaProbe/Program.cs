using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Humcp.Spike.UiaProbe;

/// <summary>
/// Walks the live UIA tree of the Phase 0 spike and asserts the mechanical half of the
/// accessibility contract (ROADMAP §9). Automated checks prove names exist; only NVDA and JAWS
/// prove the app is pleasant, so this is a floor, not a verdict.
/// </summary>
internal static class Program
{
    /// <summary>UIA_LevelPropertyId. Not in FlaUI's property library, so registered by hand.</summary>
    private static readonly PropertyId LevelProperty =
        PropertyId.Register(AutomationType.UIA3, 30154, "Level");

    private static readonly StringBuilder Report = new();
    private static int _failures;

    private static int Main(string[] args)
    {
        var exe = args.FirstOrDefault() ?? DefaultSpikePath();
        if (!File.Exists(exe))
        {
            Console.Error.WriteLine($"Spike executable not found: {exe}");
            Console.Error.WriteLine("Build spike/Humcp.Spike first, or pass the path as an argument.");
            return 2;
        }

        using var automation = new UIA3Automation();
        var app = Application.Launch(exe);
        try
        {
            var window = app.GetMainWindow(automation, TimeSpan.FromSeconds(20))
                         ?? throw new InvalidOperationException("No main window appeared.");
            Wait.UntilInputIsProcessed();
            Thread.Sleep(1200);

            Section("1. UIA tree, as a screen reader sees it");
            DumpTree(window, 0);

            Section("2. Tree hierarchy — the finding that decided against WinUI 3");
            CheckTreeLevels(window);

            Section("3. Every control has an accessible name");
            CheckNames(window);

            Section("4. Every input has help text or a label association");
            CheckInputLabelling(window);

            Section("5. Required fields are marked with IsRequiredForForm");
            CheckRequiredFlags(window);

            Section("6. Tab order reaches every field and cycles");
            CheckTabOrder(window);

            Section("7. Validation errors reach UIA via ItemStatus");
            CheckValidationSurfacing(window);

            Section("8. Date fields advertise the calendar keystroke");
            CheckDateHint(window);

            Section("9. Virtualization on a generated form (§7.6)");
            CheckVirtualization(window, automation);
        }
        catch (Exception ex)
        {
            _failures++;
            Report.AppendLine("PROBE ABORTED: " + ex);
        }
        finally
        {
            try { app.Close(); } catch { /* best effort */ }
            try { if (!app.HasExited) app.Kill(); } catch { }
        }

        Report.AppendLine();
        Report.AppendLine(_failures == 0
            ? "RESULT: all mechanical checks passed."
            : $"RESULT: {_failures} mechanical check(s) failed.");

        var text = Report.ToString();
        Console.Out.Write(text);

        var outPath = Path.Combine(AppContext.BaseDirectory, "uia-probe-report.txt");
        File.WriteAllText(outPath, text);
        Console.Out.WriteLine();
        Console.Out.WriteLine("Report written to " + outPath);

        return _failures == 0 ? 0 : 1;
    }

    private static string DefaultSpikePath()
    {
        var here = AppContext.BaseDirectory;
        var root = Path.GetFullPath(Path.Combine(here, "..", "..", "..", "..", ".."));
        return Path.Combine(root, "spike", "Humcp.Spike", "bin", "Debug", "net10.0-windows", "Humcp.Spike.exe");
    }

    // ------------------------------------------------------------------ checks

    private static void DumpTree(AutomationElement element, int depth)
    {
        if (depth > 14) return;

        var name = Safe(() => element.Name) ?? "";
        var type = Safe(() => element.ControlType.ToString()) ?? "?";
        var help = Safe(() => element.HelpText) ?? "";
        var level = LevelOf(element);

        var line = new StringBuilder()
            .Append(new string(' ', depth * 2))
            .Append(type)
            .Append("  \"").Append(Trim(name)).Append('"');

        if (!string.IsNullOrEmpty(help)) line.Append("  help=\"").Append(Trim(help, 60)).Append('"');
        if (level is { } lv) line.Append("  Level=").Append(lv);
        if (Safe(() => element.Properties.IsRequiredForForm.ValueOrDefault) == true) line.Append("  required");
        var status = Safe(() => element.Properties.ItemStatus.ValueOrDefault);
        if (!string.IsNullOrEmpty(status)) line.Append("  status=\"").Append(Trim(status, 40)).Append('"');
        if (Safe(() => element.Properties.IsOffscreen.ValueOrDefault) == true) line.Append("  offscreen");

        Report.AppendLine(line.ToString());

        foreach (var child in Children(element))
            DumpTree(child, depth + 1);
    }

    private static void CheckTreeLevels(AutomationElement window)
    {
        var tree = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Tree));
        if (tree is null) { Fail("No Tree control found."); return; }

        var items = tree.FindAllDescendants(cf => cf.ByControlType(ControlType.TreeItem));
        Report.AppendLine($"TreeItem elements in the UIA tree: {items.Length}");

        var byDepth = new SortedDictionary<int, List<string>>();
        foreach (var item in items)
        {
            var depth = StructuralDepth(item, tree);
            byDepth.TryAdd(depth, new List<string>());
            byDepth[depth].Add(Trim(Safe(() => item.Name) ?? "", 44));
        }

        foreach (var (depth, names) in byDepth)
            Report.AppendLine($"  structural depth {depth}: {names.Count} item(s) — {string.Join(" | ", names.Take(4))}");

        var levels = items.Select(LevelOf).Where(l => l is not null).Select(l => l!.Value).Distinct().OrderBy(x => x).ToList();
        Report.AppendLine(levels.Count == 0
            ? "  UIA Level property: not implemented by WPF. NVDA derives level from nesting, which is present above."
            : "  UIA Level property values: " + string.Join(", ", levels));

        if (byDepth.Count < 3)
            Fail($"Expected at least 3 distinct nesting depths in the tree, saw {byDepth.Count}. " +
                 "This is the WinUI 3 failure mode (everything reported at level 1).");
        else
            Pass($"Tree hierarchy survives into the UIA tree across {byDepth.Count} depths.");
    }

    private static int StructuralDepth(AutomationElement item, AutomationElement tree)
    {
        var depth = 1;
        var walker = item.Automation.TreeWalkerFactory.GetRawViewWalker();
        var current = walker.GetParent(item);
        while (current is not null && !current.Equals(tree))
        {
            if (Safe(() => current!.ControlType) == ControlType.TreeItem) depth++;
            current = walker.GetParent(current);
        }
        return depth;
    }

    private static void CheckNames(AutomationElement window)
    {
        var missing = new List<string>();
        var chrome = new List<string>();
        var checkedCount = 0;

        foreach (var e in Descendants(window))
        {
            var type = Safe(() => e.ControlType);
            var named = !string.IsNullOrWhiteSpace(Safe(() => e.Name));

            // WPF reports its own title bar and status bar as focusable, but Tab never lands on
            // them; they are chrome either way.
            var isChrome = type is ControlType.TitleBar or ControlType.StatusBar;

            // Anything else the keyboard can land on is ours to get right.
            if (!isChrome && Safe(() => e.Properties.IsKeyboardFocusable.ValueOrDefault) == true)
            {
                checkedCount++;
                if (!named) missing.Add($"{type} (automationId={Safe(() => e.AutomationId)})");
                continue;
            }

            // Framework chrome — worth reporting, not worth failing over.
            if (!named && (isChrome || type is ControlType.Button))
                chrome.Add($"{type} (automationId={Safe(() => e.AutomationId)})");
        }

        Report.AppendLine($"Keyboard-focusable elements checked: {checkedCount}");
        if (chrome.Count > 0)
            Report.AppendLine($"NOTE: {chrome.Count} unnamed, unfocusable WPF chrome element(s): " +
                              string.Join(", ", chrome.Distinct().Take(6)));

        if (missing.Count > 0)
            Fail($"{missing.Count} focusable element(s) with an empty Name: {string.Join(", ", missing.Take(10))}");
        else
            Pass("Every keyboard-focusable element has a non-empty Name.");
    }

    private static void CheckInputLabelling(AutomationElement window)
    {
        var bad = new List<string>();
        var count = 0;

        foreach (var e in Descendants(window))
        {
            var type = Safe(() => e.ControlType);
            if (type is not (ControlType.Edit or ControlType.ComboBox or ControlType.CheckBox)) continue;
            count++;

            var help = Safe(() => e.HelpText) ?? "";
            var labelledBy = Safe(() => e.Properties.LabeledBy.ValueOrDefault);
            if (string.IsNullOrWhiteSpace(help) && labelledBy is null)
                bad.Add(Trim(Safe(() => e.Name) ?? "(unnamed)", 40));
        }

        Report.AppendLine($"Inputs checked: {count}");
        if (bad.Count > 0) Fail($"{bad.Count} input(s) with neither HelpText nor LabeledBy: {string.Join(", ", bad)}");
        else Pass("Every input carries HelpText or a LabeledBy association.");
    }

    private static void CheckRequiredFlags(AutomationElement window)
    {
        var required = Descendants(window)
            .Where(e => Safe(() => e.Properties.IsRequiredForForm.ValueOrDefault) == true)
            .Select(e => Trim(Safe(() => e.Name) ?? "", 40))
            .ToList();

        Report.AppendLine("Fields flagged required: " + (required.Count == 0 ? "(none)" : string.Join(", ", required)));

        if (required.Count < 2)
            Fail("Expected at least 2 required fields (Repository, State) to expose IsRequiredForForm.");
        else if (required.Any(n => n.Contains("required", StringComparison.OrdinalIgnoreCase)))
            Fail("A required field has \"required\" baked into its Name — §7.4 forbids this.");
        else
            Pass("Required fields are flagged via IsRequiredForForm, not by name suffix.");
    }

    private static void CheckTabOrder(AutomationElement window)
    {
        // Never send synthetic keystrokes unless the spike genuinely owns the foreground — they
        // would otherwise land in whatever the person at this machine is doing.
        var pid = Safe(() => window.Properties.ProcessId.ValueOrDefault);
        window.AsWindow().SetForeground();
        window.Focus();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(500);

        var start = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("Filter")));
        start?.Focus();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(200);

        if (Safe(() => window.Automation.FocusedElement()?.Properties.ProcessId.ValueOrDefault) != pid)
        {
            Fail("The spike window never took the foreground, so the tab-order walk was skipped " +
                 "rather than typing into another application.");
            return;
        }

        var seen = new List<string>();
        var seenSet = new HashSet<string>();
        string? firstKey = null;
        var cycled = false;

        for (var i = 0; i < 80; i++)
        {
            Keyboard.Press(VirtualKeyShort.TAB);
            Keyboard.Release(VirtualKeyShort.TAB);
            Wait.UntilInputIsProcessed();
            Thread.Sleep(40);

            var focused = window.Automation.FocusedElement();
            if (focused is null) continue;
            if (Safe(() => focused.Properties.ProcessId.ValueOrDefault) != pid)
            {
                Fail("Focus left the spike process during the tab walk; stopping so keystrokes " +
                     "do not leak into another application.");
                return;
            }

            var key = $"{Safe(() => focused.ControlType)}:{Trim(Safe(() => focused.Name) ?? "", 40)}";
            firstKey ??= key;

            if (i > 0 && key == firstKey) { cycled = true; break; }
            if (seenSet.Add(key)) seen.Add(key);
        }

        Report.AppendLine($"Distinct stops reached by Tab: {seen.Count}");
        foreach (var s in seen.Take(30)) Report.AppendLine("  " + s);
        if (seen.Count > 30) Report.AppendLine($"  … and {seen.Count - 30} more");

        var reachedEdits = seen.Count(s => s.StartsWith("Edit:"));
        if (reachedEdits < 5) Fail($"Tab reached only {reachedEdits} edit control(s); the generated form has more.");
        else Pass($"Tab reached {reachedEdits} edit controls.");

        // Nothing chrome-like belongs in the tab order. A menu bar is reached with Alt and then
        // arrowed; a splitter is reached from a command, not by tabbing past it every time.
        var chromeStops = seen.Where(s => s.StartsWith("Thumb:") || s.StartsWith("MenuItem:")).ToList();
        if (chromeStops.Count > 0)
            Fail($"{chromeStops.Count} chrome element(s) in the tab order: {string.Join(", ", chromeStops)}");
        else
            Pass("No splitters or menu items in the tab order.");

        Report.AppendLine(cycled
            ? "Tab order cycles back to its starting point."
            : "NOTE: no cycle observed within 80 tab presses — check manually.");
    }

    private static void CheckValidationSurfacing(AutomationElement window)
    {
        var run = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Run tool")))
                  ?.AsButton();
        if (run is null) { Fail("Could not find the Run tool button."); return; }

        run.Invoke();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(600);

        var withStatus = Descendants(window)
            .Where(e => !string.IsNullOrEmpty(Safe(() => e.Properties.ItemStatus.ValueOrDefault)))
            .Select(e => $"{Trim(Safe(() => e.Name) ?? "", 30)} → {Trim(Safe(() => e.Properties.ItemStatus.ValueOrDefault) ?? "", 50)}")
            .ToList();

        foreach (var s in withStatus) Report.AppendLine("  " + s);

        var summary = window.FindFirstDescendant(cf => cf.ByName("Validation errors"));
        Report.AppendLine("Error summary list present: " + (summary is not null));

        var focused = window.Automation.FocusedElement();
        Report.AppendLine("Focus after a failed submit: " + Trim(Safe(() => focused?.Name) ?? "(none)", 40));

        if (withStatus.Count == 0) Fail("No element exposed ItemStatus after a failed submit.");
        else if (summary is null) Fail("No navigable error summary list was exposed.");
        else Pass($"{withStatus.Count} field(s) exposed ItemStatus, and the error summary list is in the tree.");
    }

    /// <summary>
    /// A DatePicker's calendar opens with Alt+Down Arrow and nothing announces that. The hint has
    /// to reach the inner Edit, because that is where focus actually lands.
    /// </summary>
    private static void CheckDateHint(AutomationElement window)
    {
        var edit = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit).And(cf.ByName("Updated since")))
            .FirstOrDefault();
        if (edit is null) { Fail("Could not find the date field's inner edit control."); return; }

        var help = Safe(() => edit.HelpText) ?? "";
        Report.AppendLine($"  Updated since → help=\"{Trim(help, 120)}\"");

        if (help.Contains("Alt plus Down Arrow", StringComparison.OrdinalIgnoreCase))
            Pass("The date field's focusable edit advertises Alt+Down Arrow.");
        else
            Fail("The calendar keystroke hint did not reach the focusable edit inside the DatePicker.");
    }

    private static void CheckVirtualization(AutomationElement window, UIA3Automation automation)
    {
        var add = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button).And(cf.ByName("Add 200 fields")))
                  ?.AsButton();
        if (add is null) { Fail("Could not find the Add 200 fields button."); return; }

        add.Invoke();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(1500);

        var withoutVirtualization = CountEdits(window);
        Report.AppendLine($"Edit controls visible to UIA, virtualization OFF: {withoutVirtualization}");

        if (!ToggleVirtualizationMenu(window, true))
        {
            Fail("Could not toggle virtualization through the View menu.");
            return;
        }
        Thread.Sleep(1500);

        var withVirtualization = CountEdits(window);
        Report.AppendLine($"Edit controls visible to UIA, virtualization ON:  {withVirtualization}");

        ToggleVirtualizationMenu(window, false);

        var lost = withoutVirtualization - withVirtualization;
        if (lost > 0)
            Pass($"Confirmed §7.6: turning virtualization on removed {lost} field(s) from the accessibility tree. " +
                 "Generated forms must keep IsVirtualizing=False.");
        else
            Report.AppendLine("NOTE: virtualization did not reduce the UIA element count in this run. " +
                              "Re-check with a smaller window or more fields before relaxing the §7.6 rule.");
    }

    private static int CountEdits(AutomationElement window)
        => window.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit)).Length;

    private static bool ToggleVirtualizationMenu(AutomationElement window, bool on)
    {
        var menuBar = window.FindFirstDescendant(cf => cf.ByControlType(ControlType.Menu));
        var view = menuBar?.FindFirstDescendant(cf => cf.ByName("View"));
        if (view is null) return false;

        view.AsMenuItem().Click();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(400);

        var item = window.Automation.GetDesktop()
            .FindFirstDescendant(cf => cf.ByName("Virtualize the generated form (breaks UIA)"));
        if (item is null)
        {
            Keyboard.Press(VirtualKeyShort.ESCAPE);
            Keyboard.Release(VirtualKeyShort.ESCAPE);
            return false;
        }

        item.AsMenuItem().Click();
        Wait.UntilInputIsProcessed();
        Thread.Sleep(300);
        return true;
    }

    // ------------------------------------------------------------------ helpers

    private static IEnumerable<AutomationElement> Children(AutomationElement element)
    {
        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { yield break; }
        foreach (var c in children) yield return c;
    }

    private static IEnumerable<AutomationElement> Descendants(AutomationElement root)
    {
        foreach (var child in Children(root))
        {
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    private static int? LevelOf(AutomationElement element)
    {
        try
        {
            var value = element.FrameworkAutomationElement.GetPropertyValue(LevelProperty);
            return value is int i ? i : null;
        }
        catch { return null; }
    }

    private static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }

    private static string Trim(string text, int max = 70)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    private static void Section(string title)
    {
        Report.AppendLine();
        Report.AppendLine("=== " + title + " " + new string('=', Math.Max(0, 68 - title.Length)));
    }

    private static void Pass(string message)
    {
        Report.AppendLine("PASS: " + message);
    }

    private static void Fail(string message)
    {
        _failures++;
        Report.AppendLine("FAIL: " + message);
    }
}
