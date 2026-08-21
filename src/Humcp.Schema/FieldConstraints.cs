using System.Globalization;

namespace Humcp.Schema;

/// <summary>
/// The limits a schema puts on one value. Kept as data rather than as a validator, because two
/// different things need them: <see cref="SchemaValidator"/> checks against them, and the renderer
/// <em>says</em> them — a constraint nobody mentions is a trap for someone who cannot see the box
/// turn red.
/// </summary>
public sealed record FieldConstraints
{
    /// <summary>The schema said nothing.</summary>
    public static readonly FieldConstraints None = new();

    public int? MinLength { get; init; }

    public int? MaxLength { get; init; }

    /// <summary>A regular expression, as the schema wrote it. Never spoken: it is not language.</summary>
    public string? Pattern { get; init; }

    /// <summary>
    /// The <c>format</c> annotation — <c>email</c>, <c>uri</c>, <c>uuid</c>, and the rest. Kept even
    /// when it did not change the control, because it is worth saying and worth warning about.
    /// </summary>
    public string? Format { get; init; }

    public double? Minimum { get; init; }

    public double? Maximum { get; init; }

    public double? ExclusiveMinimum { get; init; }

    public double? ExclusiveMaximum { get; init; }

    public double? MultipleOf { get; init; }

    public int? MinItems { get; init; }

    public int? MaxItems { get; init; }

    public bool UniqueItems { get; init; }

    public bool IsEmpty =>
        MinLength is null && MaxLength is null && Pattern is null && Format is null &&
        Minimum is null && Maximum is null && ExclusiveMinimum is null && ExclusiveMaximum is null &&
        MultipleOf is null && MinItems is null && MaxItems is null && !UniqueItems;

    /// <summary>
    /// The constraints as a sentence to be heard, or <c>""</c> when there is nothing to say. Phase 3
    /// appends this to <c>HelpText</c> where the control does not already announce it.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>();

        AddLength(parts);
        AddRange(parts);

        if (MultipleOf is { } step)
            parts.Add($"a multiple of {Number(step)}");

        AddItems(parts);

        if (UniqueItems) parts.Add("no duplicates");

        if (Format is { Length: > 0 } format && DescribeFormat(format) is { Length: > 0 } described)
            parts.Add(described);

        return parts.Count == 0 ? "" : Capitalise(string.Join(", ", parts)) + ".";
    }

    private void AddLength(List<string> parts)
    {
        switch (MinLength, MaxLength)
        {
            case ({ } min, { } max) when min == max: parts.Add($"exactly {Characters(min)}"); break;
            case ({ } min, { } max): parts.Add($"{min} to {Characters(max)}"); break;
            case ({ } min, null): parts.Add($"at least {Characters(min)}"); break;
            case (null, { } max): parts.Add($"up to {Characters(max)}"); break;
            default: break;
        }
    }

    private void AddRange(List<string> parts)
    {
        var lower = Minimum is { } min ? $"at least {Number(min)}"
                  : ExclusiveMinimum is { } exclusiveMin ? $"greater than {Number(exclusiveMin)}"
                  : null;

        var upper = Maximum is { } max ? $"at most {Number(max)}"
                  : ExclusiveMaximum is { } exclusiveMax ? $"less than {Number(exclusiveMax)}"
                  : null;

        if (lower is not null && upper is not null) parts.Add($"{lower} and {upper}");
        else if (lower is not null) parts.Add(lower);
        else if (upper is not null) parts.Add(upper);
    }

    private void AddItems(List<string> parts)
    {
        switch (MinItems, MaxItems)
        {
            case ({ } min, { } max) when min == max: parts.Add($"exactly {Items(min)}"); break;
            case ({ } min, { } max): parts.Add($"{min} to {Items(max)}"); break;
            case ({ } min, null) when min > 0: parts.Add($"at least {Items(min)}"); break;
            case (null, { } max): parts.Add($"up to {Items(max)}"); break;
            default: break;
        }
    }

    /// <summary>
    /// Plain words for the formats worth mentioning. An unknown format is not described: the schema
    /// is third-party and <c>format</c> is an open vocabulary, so inventing a sentence for
    /// <c>x-internal-id</c> would be putting words in a stranger's mouth.
    /// </summary>
    private static string DescribeFormat(string format) => format switch
    {
        "email" or "idn-email" => "an email address",
        "uri" or "iri" => "a URL",
        "uri-reference" or "iri-reference" => "a URL or a path",
        "hostname" or "idn-hostname" => "a host name",
        "ipv4" => "an IPv4 address",
        "ipv6" => "an IPv6 address",
        "uuid" => "a UUID",
        "date" => "a date",
        "time" => "a time of day",
        "date-time" => "a date and time",
        "duration" => "a duration, such as PT30M",
        "regex" => "a regular expression",
        "json-pointer" or "relative-json-pointer" => "a JSON pointer",
        _ => "",
    };

    private static string Characters(int count) => count == 1 ? "1 character" : $"{count} characters";

    private static string Items(int count) => count == 1 ? "1 item" : $"{count} items";

    private static string Number(double value) =>
        value.ToString(value == Math.Floor(value) && Math.Abs(value) < 1e15 ? "0" : "0.############",
            CultureInfo.CurrentCulture);

    private static string Capitalise(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
}
