using System.Collections;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Data;

namespace Humcp.App;

/// <summary>
/// Adds a sentence to a field's help text that the schema had no way to know — what keystroke the
/// control it became answers to (ROADMAP §7.15).
/// </summary>
/// <remarks>
/// Applied in the template rather than in <c>Humcp.Schema</c> on purpose. The schema said
/// <c>format: date</c> and has no idea what widget that turned into; the hint belongs to the widget,
/// and travels with whichever one a tier decision picks. Keystrokes are spelled out — "Alt plus
/// Down Arrow" — because the string is spoken and screen readers differ on whether they announce a
/// plus sign at all.
/// </remarks>
public sealed class AppendHintConverter : IValueConverter
{
    public string Hint { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").TrimEnd();
        var hint = parameter as string ?? Hint;

        if (text.Length == 0) return hint;
        if (hint.Length == 0) return text;

        var separator = text[^1] is '.' or '!' or '?' or ':' ? " " : ". ";
        return text + separator + hint;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Marks one character of a label as its access key, in the underscore notation WPF reads
/// (ROADMAP §7.3).
/// </summary>
/// <remarks>
/// Which character was decided in <c>Humcp.Schema</c>, over the whole form at once so that no two
/// fields claim the same key. This is only the spelling of that decision — and the escaping, since
/// a label that already contains an underscore would otherwise silently lose it and swallow the
/// letter after it.
/// </remarks>
public sealed class AccessKeyConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var label = values is [string text, ..] ? text : "";
        var index = values is [_, int position, ..] ? position : -1;

        var marked = new StringBuilder(label.Length + 2);

        for (var i = 0; i < label.Length; i++)
        {
            if (i == index) marked.Append('_');

            marked.Append(label[i]);
            if (label[i] == '_') marked.Append('_');
        }

        return marked.ToString();
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Collapses whatever it is bound to when there is nothing there — an empty collection, a zero, a
/// blank string, a null.
/// </summary>
/// <remarks>
/// Collapsed rather than hidden, every time. A hidden element is still in the automation tree and
/// still in the tab order, which is the difference between a control a screen reader does not
/// mention and one it announces as being there with nothing in it.
/// </remarks>
public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var empty = value switch
        {
            null => true,
            string text => text.Trim().Length == 0,
            int count => count == 0,
            bool flag => !flag,
            ICollection collection => collection.Count == 0,
            IEnumerable sequence => !sequence.GetEnumerator().MoveNext(),
            _ => false,
        };

        return empty ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
