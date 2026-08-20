using System.Globalization;
using System.Windows.Data;

namespace Humcp.Spike;

/// <summary>
/// Appends a keystroke hint to a field's help text.
///
/// Phase 0 finding: a WPF <c>DatePicker</c>'s calendar opens with Alt+Down Arrow, and nothing —
/// not the framework, not NVDA — ever says so. The hint belongs to the *control choice*, so the
/// renderer adds it; the schema knows nothing about which widget it ended up as.
///
/// Wording note: the hint is spoken, so "plus" is written out. Screen readers differ on whether
/// they announce "+" at all, depending on the user's punctuation level.
/// </summary>
public sealed class AppendHintConverter : IValueConverter
{
    public string Hint { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").TrimEnd();
        if (text.Length == 0) return Hint;
        if (Hint.Length == 0) return text;

        var separator = text[^1] is '.' or '!' or '?' or ':' ? " " : ". ";
        return text + separator + Hint;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
