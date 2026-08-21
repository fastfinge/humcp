using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;

namespace Humcp.App.UiTests;

/// <summary>
/// Walking helpers for the live UIA tree, carried over from the Phase 0 probe
/// (phase0-findings §7.3) — including the one that matters most: depth is counted by nesting,
/// because WPF does not implement the UIA <c>Level</c> property.
/// </summary>
internal static class UiaTree
{
    /// <summary>UIA_LevelPropertyId. Not in FlaUI's property library, so registered by hand.</summary>
    public static readonly PropertyId LevelProperty =
        PropertyId.Register(AutomationType.UIA3, 30154, "Level");

    public static IEnumerable<AutomationElement> Children(AutomationElement element)
    {
        AutomationElement[] children;
        try { children = element.FindAllChildren(); }
        catch { yield break; }

        foreach (var child in children) yield return child;
    }

    public static IEnumerable<AutomationElement> Descendants(AutomationElement root)
    {
        foreach (var child in Children(root))
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    /// <summary>
    /// How deep a tree item is, counted by nested <c>TreeItem</c> ancestors. Never ask WPF for
    /// <see cref="LevelProperty"/>: it does not implement it, and asserting on it would pass
    /// vacuously (ROADMAP §2.1, §9).
    /// </summary>
    public static int StructuralDepth(AutomationElement item, AutomationElement tree)
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

    public static int? LevelOf(AutomationElement element)
    {
        try
        {
            var value = element.FrameworkAutomationElement.GetPropertyValue(LevelProperty);
            return value is int level ? level : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The tree as a screen reader sees it. The artefact that made Phase 0's findings obvious.</summary>
    public static string Dump(AutomationElement root)
    {
        var sb = new StringBuilder();
        Dump(root, 0, sb);
        return sb.ToString();
    }

    private static void Dump(AutomationElement element, int depth, StringBuilder sb)
    {
        if (depth > 14) return;

        var line = new StringBuilder()
            .Append(new string(' ', depth * 2))
            .Append(Safe(() => element.ControlType.ToString()) ?? "?")
            .Append("  \"").Append(Flatten(Safe(() => element.Name) ?? "")).Append('"');

        var help = Safe(() => element.HelpText) ?? "";
        if (help.Length > 0) line.Append("  help=\"").Append(Flatten(help, 60)).Append('"');

        if (Safe(() => element.Properties.IsKeyboardFocusable.ValueOrDefault) == true) line.Append("  focusable");
        if (Safe(() => element.Properties.IsRequiredForForm.ValueOrDefault) == true) line.Append("  required");

        var status = Safe(() => element.Properties.ItemStatus.ValueOrDefault);
        if (!string.IsNullOrEmpty(status)) line.Append("  status=\"").Append(Flatten(status, 40)).Append('"');

        sb.AppendLine(line.ToString());

        foreach (var child in Children(element)) Dump(child, depth + 1, sb);
    }

    public static string Flatten(string text, int max = 80)
    {
        var flat = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
        return flat.Length <= max ? flat : flat[..(max - 1)] + "…";
    }

    public static T? Safe<T>(Func<T> get)
    {
        try { return get(); }
        catch { return default; }
    }
}
