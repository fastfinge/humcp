using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Humcp.Schema.Tests;

/// <summary>
/// The corpus is what grounds this phase (ROADMAP §9). These tests ask two things of it: that
/// nothing in it defeats the engine, and that the proportion it renders natively is the number
/// Phase 2 promised.
/// </summary>
public class CorpusTests(Xunit.ITestOutputHelper output)
{
    /// <summary>ROADMAP §10, Phase 2 exit criterion.</summary>
    private const double RequiredTier12Share = 0.90;

    [Fact]
    public void The_corpus_is_real_and_large_enough_to_mean_something()
    {
        var servers = Corpus.Entries.Select(e => e.Server).Distinct().ToList();

        Assert.True(servers.Count >= 20, $"only {servers.Count} servers harvested: {string.Join(", ", servers)}");
        Assert.True(Corpus.Entries.Count >= 150, $"only {Corpus.Entries.Count} tools harvested");
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_real_schema_produces_a_form(string key)
    {
        // The invariant, stated as plainly as it can be: there is no tool in the world of published
        // MCP servers that this engine refuses to render (ROADMAP §5).
        var form = FormBuilder.Build(Corpus.Get(key).InputSchema);

        Assert.NotNull(form);
        Assert.All(form.AllFields(), field =>
        {
            Assert.NotEqual("", field.Label);
            Assert.InRange((int)field.Tier, 1, 3);

            // Tier 3 is allowed. Tier 3 without a reason is not: §7.18 says an edge explains itself,
            // and this reason is the sentence the user hears.
            if (field.Tier == FieldTier.Raw) Assert.False(string.IsNullOrWhiteSpace(field.DegradedReason));
            else Assert.Null(field.DegradedReason);
        });
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void Nothing_a_server_wrote_reaches_the_user_unsanitised(string key)
    {
        var entry = Corpus.Get(key);
        var form = FormBuilder.Build(entry.InputSchema);

        foreach (var field in form.AllFields())
        {
            AssertSpeakable(field.Label, TextSanitizer.NameLength, $"{entry}:{field.Path} label");
            AssertSpeakable(field.HelpText, TextSanitizer.HelpLength * 2, $"{entry}:{field.Path} help");

            foreach (var choice in field.Choices)
                AssertSpeakable(choice.Label, TextSanitizer.NameLength, $"{entry}:{field.Path} choice");
        }
    }

    [Theory]
    [MemberData(nameof(Everything))]
    public void Every_real_schema_can_be_validated_against_without_throwing(string key)
    {
        var entry = Corpus.Get(key);
        var form = FormBuilder.Build(entry.InputSchema);

        // Three shapes of input that a form will really produce: nothing filled in, something of
        // the wrong type in every slot, and a value that is not even an object.
        foreach (var value in new JsonNode?[] { new JsonObject(), Nonsense(form), JsonValue.Create(42) })
        {
            var report = SchemaValidator.Validate(form, value);

            Assert.NotNull(report);
            Assert.All(report.Issues, issue =>
            {
                Assert.False(string.IsNullOrWhiteSpace(issue.Message), $"{entry}:{issue.FieldPath}");
                Assert.EndsWith(".", issue.Message);
                AssertSpeakable(issue.Announcement, 600, $"{entry}:{issue.FieldPath}");
            });
        }
    }

    [Fact]
    public void How_much_of_the_corpus_the_validator_can_actually_check_is_written_down()
    {
        // Not an assertion about a number, because the number is a fact about other people's
        // schemas. It is here so that "we validate client-side" stays an honest claim: a schema the
        // library will not load is one where the server is the only authority there is (§5).
        var unchecked_ = new List<string>();

        foreach (var entry in Corpus.Entries)
        {
            var form = FormBuilder.Build(entry.InputSchema);
            var report = SchemaValidator.Validate(form, new JsonObject());

            if (!report.WasChecked) unchecked_.Add($"{entry} — {report.NotCheckedReason}");
        }

        output.WriteLine($"{Corpus.Entries.Count - unchecked_.Count}/{Corpus.Entries.Count} schemas are checkable.");
        foreach (var line in unchecked_) output.WriteLine("  " + line);

        Assert.True(unchecked_.Count <= Corpus.Entries.Count / 10,
            $"{unchecked_.Count} of {Corpus.Entries.Count} real schemas cannot be checked at all:\n"
            + string.Join("\n", unchecked_));
    }

    [Fact]
    public void Tier_coverage_meets_the_phase_exit_criterion()
    {
        var report = MeasureCoverage();
        output.WriteLine(report.Text);

        // Written down rather than only asserted. The number moving is the interesting event, and a
        // checked-in file makes it a line in a diff rather than something someone has to remember.
        Directory.CreateDirectory(Corpus.GoldenDirectory);
        File.WriteAllText(Path.Combine(Corpus.GoldenDirectory, "coverage.txt"), report.Text);

        Assert.True(report.TopLevelShare >= RequiredTier12Share,
            $"{report.TopLevelShare:P1} of top-level parameters render at Tier 1 or 2, " +
            $"below the {RequiredTier12Share:P0} Phase 2 exit criterion.\n\n{report.Text}");
    }

    public static TheoryData<string> Everything() => Corpus.Keys();

    // ------------------------------------------------------------------ the measurement

    private sealed record Coverage(string Text, double TopLevelShare);

    private static Coverage MeasureCoverage()
    {
        var topLevel = new int[4];
        var everything = new int[4];
        var degradations = new Dictionary<string, int>(StringComparer.Ordinal);
        var wholeFormRaw = new List<string>();
        var noArguments = 0;

        foreach (var entry in Corpus.Entries)
        {
            var form = FormBuilder.Build(entry.InputSchema);

            if (form.IsWhollyRaw) wholeFormRaw.Add($"{entry} — {form.DegradedReason}");
            if (form.IsEmpty) noArguments++;

            foreach (var field in form.Fields) topLevel[(int)field.Tier]++;

            foreach (var field in form.AllFields())
            {
                everything[(int)field.Tier]++;

                if (field.DegradedReason is { } reason)
                    degradations[reason] = degradations.GetValueOrDefault(reason) + 1;
            }
        }

        var topTotal = topLevel[1] + topLevel[2] + topLevel[3];
        var allTotal = everything[1] + everything[2] + everything[3];
        var share = topTotal == 0 ? 1 : (topLevel[1] + topLevel[2]) / (double)topTotal;

        var sb = new StringBuilder();
        sb.AppendLine("Tier coverage over the harvested corpus (ROADMAP §9).");
        sb.AppendLine("Regenerated by Humcp.Schema.Tests; do not hand-edit.");
        sb.AppendLine();
        sb.Append(Corpus.Entries.Count).Append(" tools from ")
          .Append(Corpus.Entries.Select(e => e.Server).Distinct().Count()).Append(" servers, ")
          .Append(noArguments).AppendLine(" of them taking no arguments.");
        sb.AppendLine();

        Table(sb, "Top-level parameters", topLevel, topTotal);
        sb.AppendLine();
        Table(sb, "Every field, nesting included", everything, allTotal);

        if (wholeFormRaw.Count > 0)
        {
            sb.AppendLine().Append("Tools rendered as one JSON editor (").Append(wholeFormRaw.Count).AppendLine("):");
            foreach (var tool in wholeFormRaw.Order(StringComparer.Ordinal)) sb.Append("  ").AppendLine(tool);
        }

        sb.AppendLine().AppendLine("Why anything reached Tier 3 — this is the telemetry ROADMAP §5 asks for:");
        foreach (var (reason, count) in degradations.OrderByDescending(d => d.Value).ThenBy(d => d.Key, StringComparer.Ordinal))
            sb.Append("  ").Append(count.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ").AppendLine(reason);

        if (degradations.Count == 0) sb.AppendLine("  (nothing)");

        return new Coverage(sb.ToString(), share);
    }

    private static void Table(StringBuilder sb, string caption, int[] counts, int total)
    {
        sb.Append(caption).Append(": ").Append(total).AppendLine();

        for (var tier = 1; tier <= 3; tier++)
        {
            var share = total == 0 ? 0 : counts[tier] / (double)total;
            sb.Append("  Tier ").Append(tier).Append(' ')
              .Append(((FieldTier)tier).ToString().PadRight(10))
              .Append(counts[tier].ToString(CultureInfo.InvariantCulture).PadLeft(5))
              .Append("  ").AppendLine(share.ToString("P1", CultureInfo.InvariantCulture));
        }

        var native = total == 0 ? 1 : (counts[1] + counts[2]) / (double)total;
        sb.Append("  Tier 1 or 2      ").Append(native.ToString("P1", CultureInfo.InvariantCulture)).AppendLine();
    }

    private static void AssertSpeakable(string text, int maxLength, string what)
    {
        Assert.True(text.Length <= maxLength, $"{what} is {text.Length} characters: {Preview(text)}");
        Assert.DoesNotContain(text, c => char.IsControl(c) && c is not '\n' and not '\r');
        Assert.DoesNotContain(text, c => c is '‮' or '​' or '⁦' or '⁧');
    }

    private static string Preview(string text) => text.Length <= 120 ? text : text[..120] + "…";

    /// <summary>A value of the wrong type in every top-level slot, to make the validator work.</summary>
    private static JsonNode Nonsense(FormModel form)
    {
        var value = new JsonObject();

        foreach (var field in form.Fields)
        {
            value[field.Name.Length > 0 ? field.Name : "arguments"] = field.Kind switch
            {
                FieldKind.Text or FieldKind.MultilineText or FieldKind.Choice
                    or FieldKind.Date or FieldKind.DateTime => JsonValue.Create(-1),
                _ => JsonValue.Create("not a real value at all"),
            };
        }

        return value;
    }
}
