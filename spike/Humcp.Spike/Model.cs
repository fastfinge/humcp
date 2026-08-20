using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Humcp.Spike;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

// ---------------------------------------------------------------- tree

/// <summary>A node in the server / category / group / tool tree.</summary>
public sealed class TreeNode
{
    public required string Label { get; init; }

    /// <summary>Status text folded into the accessible name, per ROADMAP §8.</summary>
    public string? Status { get; init; }

    public ObservableCollection<TreeNode> Children { get; } = new();

    /// <summary>What a screen reader should say for this node.</summary>
    public string AccessibleName => Status is null ? Label : $"{Label}, {Status}";

    /// <summary>Depth this node is expected to be announced at, for the manual checklist.</summary>
    public int ExpectedLevel { get; init; }

    public TreeNode With(params TreeNode[] children)
    {
        foreach (var c in children) Children.Add(c);
        return this;
    }
}

// ---------------------------------------------------------------- fields

public enum FieldKind
{
    Text,
    Multiline,
    Number,
    Bool,
    Enum,
    Date,
    DateAsText,
    Group,
}

/// <summary>
/// Stand-in for the real <c>FieldModel</c>. Deliberately hand-built here: Phase 0 tests WPF's
/// accessibility, not schema translation.
/// </summary>
public sealed class SpikeField : Observable
{
    public required string Name { get; init; }
    public required FieldKind Kind { get; init; }

    /// <summary>Clean label. Goes to AutomationProperties.Name — never carries "(required)".</summary>
    public required string Label { get; init; }

    /// <summary>Same label with a WPF access-key underscore inserted. Visual label only.</summary>
    public string LabelWithAccessKey { get; set; } = "";

    public string HelpText { get; init; } = "";

    /// <summary>Full, unsanitised description — what F1 would show.</summary>
    public string FullDescription { get; init; } = "";

    public bool IsRequired { get; init; }

    public IReadOnlyList<string> EnumValues { get; init; } = Array.Empty<string>();

    /// <summary>Display labels for <see cref="EnumValues"/> (JSON Schema <c>enumNames</c>).</summary>
    public IReadOnlyList<string> EnumNames { get; init; } = Array.Empty<string>();

    /// <summary>Value/display pairs for the ComboBox: display comes from enumNames, value from enum.</summary>
    public IReadOnlyList<EnumChoice> Choices => _choices ??= EnumValues
        .Select((v, i) => new EnumChoice(v, i < EnumNames.Count ? EnumNames[i] : v))
        .ToList();

    private IReadOnlyList<EnumChoice>? _choices;

    public ObservableCollection<SpikeField> Children { get; } = new();

    private object? _value;
    public object? Value
    {
        get => _value;
        set { if (Set(ref _value, value)) ItemStatus = ""; }
    }

    private bool? _boolValue;
    /// <summary>Typed accessor for the CheckBox binding.</summary>
    public bool? BoolValue
    {
        get => _boolValue;
        set { if (Set(ref _boolValue, value)) ItemStatus = ""; }
    }

    private DateTime? _dateValue;
    /// <summary>Typed accessor for the DatePicker binding.</summary>
    public DateTime? DateValue
    {
        get => _dateValue;
        set { if (Set(ref _dateValue, value)) ItemStatus = ""; }
    }

    private string _itemStatus = "";
    /// <summary>
    /// Surfaced through AutomationProperties.ItemStatus. WPF's red-border ErrorTemplate is
    /// invisible to a screen reader on its own (ROADMAP §7.5).
    /// </summary>
    public string ItemStatus
    {
        get => _itemStatus;
        set => Set(ref _itemStatus, value);
    }

    public bool HasError => !string.IsNullOrEmpty(ItemStatus);

    public IEnumerable<SpikeField> SelfAndDescendants()
    {
        yield return this;
        foreach (var c in Children)
            foreach (var d in c.SelfAndDescendants())
                yield return d;
    }
}

public sealed record EnumChoice(string Value, string Display);

public sealed record ValidationError(SpikeField Field, string Message)
{
    public string Display => $"{Field.Label}: {Message}";
}
