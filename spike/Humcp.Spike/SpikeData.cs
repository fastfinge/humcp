using System.Collections.ObjectModel;
using System.Text;

namespace Humcp.Spike;

/// <summary>
/// Hand-written stand-ins for what <c>tools/list</c> and the schema engine will produce. Phase 0
/// tests the UI framework, so nothing here talks to a server or parses a schema.
/// </summary>
public static class SpikeData
{
    // ------------------------------------------------------------------ tree

    /// <summary>
    /// Mirrors ROADMAP §8. The github branch is four levels deep (server, category, group, tool);
    /// filesystem is three. Both shapes need testing — the level announcement is the single
    /// finding that decided against WinUI 3.
    /// </summary>
    public static ObservableCollection<TreeNode> BuildTree()
    {
        var github = new TreeNode
        {
            Label = "github",
            Status = "connected, 41 tools, protocol 2026-07-28",
            ExpectedLevel = 1,
        }.With(
            new TreeNode { Label = "Tools", Status = "41 items", ExpectedLevel = 2 }.With(
                new TreeNode { Label = "Issues", Status = "3 items", ExpectedLevel = 3 }.With(
                    Tool("Create issue", 4),
                    Tool("List issues", 4),
                    Tool("Add issue comment", 4)),
                new TreeNode { Label = "Pull requests", Status = "3 items", ExpectedLevel = 3 }.With(
                    Tool("List pull requests", 4),
                    Tool("Read pull request", 4),
                    Tool("Merge pull request", 4, "destructive")),
                new TreeNode { Label = "Repositories", Status = "2 items", ExpectedLevel = 3 }.With(
                    Tool("Search repositories", 4, "read-only"),
                    Tool("Delete repository", 4, "destructive"))),
            new TreeNode { Label = "Prompts", Status = "2 items", ExpectedLevel = 2 }.With(
                Tool("Summarise a release", 3),
                Tool("Draft a changelog", 3)),
            new TreeNode { Label = "Resources", Status = "1 item", ExpectedLevel = 2 }.With(
                Tool("repo://github/readme", 3)));

        var filesystem = new TreeNode
        {
            Label = "filesystem",
            Status = "connected, 12 tools, protocol 2025-11-25",
            ExpectedLevel = 1,
        }.With(
            new TreeNode { Label = "Tools", Status = "4 items", ExpectedLevel = 2 }.With(
                Tool("Read file", 3, "read-only"),
                Tool("Write file", 3),
                Tool("List directory", 3, "read-only"),
                Tool("Move file", 3)));

        var postgres = new TreeNode
        {
            Label = "postgres",
            Status = "error, process exited with code 1",
            ExpectedLevel = 1,
        };

        return new ObservableCollection<TreeNode> { github, filesystem, postgres };
    }

    private static TreeNode Tool(string label, int level, string? safety = null)
        => new() { Label = label, Status = safety, ExpectedLevel = level };

    public static IEnumerable<TreeNode> Flatten(TreeNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var d in Flatten(child))
                yield return d;
    }

    /// <summary>Keeps a node if it matches, or if anything beneath it does.</summary>
    public static ObservableCollection<TreeNode> Filter(IEnumerable<TreeNode> roots, string text)
    {
        var result = new ObservableCollection<TreeNode>();
        foreach (var node in roots)
        {
            var kept = FilterNode(node, text);
            if (kept is not null) result.Add(kept);
        }
        return result;
    }

    private static TreeNode? FilterNode(TreeNode node, string text)
    {
        var matches = node.Label.Contains(text, StringComparison.OrdinalIgnoreCase);
        var copy = new TreeNode { Label = node.Label, Status = node.Status, ExpectedLevel = node.ExpectedLevel };

        foreach (var child in node.Children)
        {
            var kept = FilterNode(child, text);
            if (kept is not null) copy.Children.Add(kept);
        }

        return matches || copy.Children.Count > 0 ? copy : null;
    }

    // ------------------------------------------------------------------ form

    /// <summary>
    /// Eight fields covering every Tier 1 control plus one Tier 2 group — the six the roadmap asks
    /// for, plus a DatePicker/plain-text pair so the manual test can compare them directly.
    /// </summary>
    public static List<SpikeField> BuildFields(int extra)
    {
        var fields = new List<SpikeField>
        {
            new()
            {
                Name = "repository",
                Kind = FieldKind.Text,
                Label = "Repository",
                IsRequired = true,
                HelpText = "Owner and repository name, for example anthropics/claude-code.",
                FullDescription =
                    "The repository to query, in owner/repo form.\n\n" +
                    "IMPORTANT: always call search_repositories first if the user has not given an " +
                    "exact repository name. Do not guess. Example: {\"repository\": \"octocat/hello-world\"}. " +
                    "This is the kind of LLM-facing prose that pitfall 1 is about: the first sentence " +
                    "becomes HelpText and the rest waits behind F1.",
            },
            new()
            {
                Name = "state",
                Kind = FieldKind.Enum,
                Label = "State",
                IsRequired = true,
                HelpText = "Which pull requests to return.",
                FullDescription = "Filter by pull request state.",
                EnumValues = new[] { "open", "closed", "all" },
                EnumNames = new[] { "Open", "Closed", "All" },
            },
            new()
            {
                Name = "limit",
                Kind = FieldKind.Number,
                Label = "Limit",
                HelpText = "Maximum number of results, 1 to 100.",
                FullDescription = "Maximum number of results to return. Minimum 1, maximum 100, default 30.",
            },
            new()
            {
                Name = "include_drafts",
                Kind = FieldKind.Bool,
                Label = "Include drafts",
                HelpText = "Include draft pull requests in the results.",
                FullDescription = "Include draft pull requests in the results. Defaults to false.",
            },
            new()
            {
                Name = "body",
                Kind = FieldKind.Multiline,
                Label = "Comment body",
                HelpText = "Markdown body to post with the query.",
                FullDescription = "Markdown body. maxLength 5000, so it renders multiline per §5.",
            },
            new()
            {
                Name = "since",
                Kind = FieldKind.Date,
                Label = "Updated since",
                HelpText = "Only pull requests updated on or after this date.",
                FullDescription = "format: date. Rendered with a WPF DatePicker — on trial in this spike.",
            },
            new()
            {
                Name = "until",
                Kind = FieldKind.DateAsText,
                Label = "Updated before",
                HelpText = "ISO 8601 date, for example 2026-08-20.",
                FullDescription =
                    "The same semantic field as “Updated since”, rendered as a plain text box instead. " +
                    "Compare the two by ear before deciding which one Phase 3 ships.",
            },
        };

        var group = new SpikeField
        {
            Name = "filters",
            Kind = FieldKind.Group,
            Label = "Advanced filters",
            HelpText = "A nested object, rendered as a real UIA group.",
            FullDescription = "Tier 2: an object schema becomes a GroupBox containing its own fields.",
        };
        group.Children.Add(new SpikeField
        {
            Name = "filters.author",
            Kind = FieldKind.Text,
            Label = "Author",
            HelpText = "GitHub login of the pull request author.",
            FullDescription = "Filter by author login.",
        });
        group.Children.Add(new SpikeField
        {
            Name = "filters.min_comments",
            Kind = FieldKind.Number,
            Label = "Minimum comments",
            HelpText = "Only pull requests with at least this many comments.",
            FullDescription = "Filter by comment count.",
        });
        fields.Add(group);

        for (var i = 1; i <= extra; i++)
        {
            fields.Add(new SpikeField
            {
                Name = $"generated_{i}",
                Kind = FieldKind.Text,
                Label = $"Generated field {i}",
                HelpText = $"Field {i}, added at runtime to test dynamic addition and tab order.",
                FullDescription = "Added after the window was built.",
            });
        }

        return fields;
    }

    // ------------------------------------------------------------------ result

    public static string RenderResult(IEnumerable<SpikeField> fields)
    {
        var sb = new StringBuilder();
        sb.AppendLine("resultType: complete");
        sb.AppendLine("content[0]: text/plain");
        sb.AppendLine();
        sb.AppendLine("Arguments sent:");
        sb.AppendLine("{");

        foreach (var f in fields.SelectMany(x => x.SelfAndDescendants()).Where(f => f.Kind != FieldKind.Group))
        {
            var value = f.Kind switch
            {
                FieldKind.Bool => f.BoolValue is true ? "true" : "false",
                FieldKind.Date => f.DateValue is { } d ? $"\"{d:yyyy-MM-dd}\"" : "null",
                FieldKind.Number => string.IsNullOrWhiteSpace(f.Value as string) ? "null" : (string)f.Value!,
                _ => string.IsNullOrWhiteSpace(f.Value as string) ? "null" : $"\"{((string)f.Value!).Replace("\"", "\\\"")}\"",
            };
            sb.AppendLine($"  \"{f.Name}\": {value},");
        }

        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rows:");
        for (var i = 1; i <= 3; i++)
            sb.AppendLine($"  #{1000 + i}  open   Fix the tree level announcement  ({i} comments)");

        return sb.ToString();
    }
}
