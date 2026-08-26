using System.Text.Json;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// What comes back from a call, over a real pipe to a real child process (ROADMAP §10, Phase 4).
/// </summary>
/// <remarks>
/// Phase 4's exit criterion is that every kind of content has a defined, non-degenerate
/// presentation, and the first half of that claim is testable without a window: every kind has to
/// arrive as something the UI can show. This file is the checklist for the crossing — one test per
/// kind, all of them against what the fixture really put on the wire rather than what a fake shared
/// with our own serialiser would agree to (§9, and pitfall 10).
/// </remarks>
public class ResultMappingTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Tool(string name)
        => Connection.Tools.SingleOrDefault(t => t.Name == name)
           ?? throw new InvalidOperationException($"The fixture published no tool called {name}.");

    private Task<ToolCallOutcome> Call(string tool)
        => Connection.CallAsync(Tool(tool), TestContext.Current.CancellationToken);

    // ------------------------------------------------------------------ structured content

    [Fact]
    public void A_published_output_schema_crosses_with_the_tool()
    {
        var tool = Tool("result_weather");

        Assert.True(tool.DescribesItsOutput);
        Assert.Equal(JsonValueKind.Object, tool.OutputSchema!.Value.ValueKind);
    }

    [Fact]
    public async Task Structured_content_arrives_and_renders_with_the_servers_own_names()
    {
        var tool = Tool("result_weather");
        var outcome = await Connection.CallAsync(tool, TestContext.Current.CancellationToken);

        Assert.True(outcome.HasOwnStructuredContent);

        var node = ResultBuilder.Build(tool.OutputSchema, outcome.Structured!.Value, "Structured result");

        Assert.Equal(ResultShape.Properties, node.Shape);

        // The schema's title, not the property name humanised: this is the whole benefit of a
        // server publishing one.
        Assert.Contains(node.Children, c => c.Label == "Temperature in celsius");
        Assert.Contains(node.Children, c => c.HelpText == "What the sky is doing.");
    }

    [Fact]
    public async Task An_array_of_objects_off_the_wire_becomes_a_table()
    {
        var tool = Tool("result_table");
        var outcome = await Connection.CallAsync(tool, new() { ["rows"] = 8 },
            TestContext.Current.CancellationToken);

        // The server wraps its list in an object because SEP-2106 asks it to; the renderer takes
        // that wrapper off and keeps the name, so what the user meets is the table.
        var table = ResultBuilder.Build(tool.OutputSchema, outcome.Structured!.Value, "Structured result");

        Assert.Equal(ResultShape.Table, table.Shape);
        Assert.Equal("Entities", table.Label);
        Assert.Equal(8, table.TotalRows);
        Assert.Equal(["name", "kind", "observations", "score", "active"], table.Columns.Select(c => c.Name));

        // A cell holding a list keeps the list (see ResultCell.Detail).
        Assert.Equal("2 items", table.Rows[0].Cells[2].Text);
        Assert.True(table.Rows[0].Cells[2].HasDetail);
    }

    [Fact]
    public async Task Structured_content_that_repeats_the_text_is_recognised_on_the_wire()
    {
        // The filesystem server's shape, and 13 of the corpus's 25 output schemas. Rendering both
        // halves would say everything twice.
        var outcome = await Call("result_wrapped");

        Assert.True(outcome.StructuredRepeatsText);
        Assert.False(outcome.HasOwnStructuredContent);
    }

    // ------------------------------------------------------------------ content parts

    [Fact]
    public async Task Every_kind_of_part_arrives_as_something_a_window_can_show()
    {
        var outcome = await Call("result_mixed");

        Assert.Collection(outcome.Blocks,
            b => Assert.IsType<TextResultBlock>(b),
            b => Assert.Equal(MediaKind.Image, Assert.IsType<MediaResultBlock>(b).Kind),
            b => Assert.Equal(MediaKind.Audio, Assert.IsType<MediaResultBlock>(b).Kind),
            b => Assert.IsType<LinkResultBlock>(b),
            b => Assert.IsType<ResourceTextResultBlock>(b),
            b => Assert.Equal(MediaKind.File, Assert.IsType<MediaResultBlock>(b).Kind));

        // Not one of them is a shrug: each says what it is, in words.
        Assert.All(outcome.Blocks, b => Assert.NotEqual("", b.Heading));
    }

    [Fact]
    public async Task A_sound_arrives_with_its_bytes_and_a_name_to_save_it_under()
    {
        var outcome = await Call("result_mixed");
        var audio = outcome.Blocks.OfType<MediaResultBlock>().Single(b => b.Kind == MediaKind.Audio);

        Assert.Equal("audio/wav", audio.MimeType);
        Assert.Equal("audio.wav", audio.SuggestedFileName);
        Assert.True(audio.CanOpen);
        Assert.Equal(2044, audio.Data.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(audio.Data.Span[..4]));
    }

    [Fact]
    public async Task An_embedded_text_resource_is_text_and_an_embedded_blob_is_a_file()
    {
        var outcome = await Call("result_mixed");

        var text = outcome.Blocks.OfType<ResourceTextResultBlock>().Single();
        Assert.Equal("humcp://demo/notes.txt", text.Uri);
        Assert.Contains("wants a caret", text.Text);

        var blob = outcome.Blocks.OfType<MediaResultBlock>().Single(b => b.Kind == MediaKind.File);
        Assert.Equal("humcp://demo/blob.dat", blob.Uri);
        Assert.Equal(8, blob.Data.Length);

        // An unrecognised type is saved as .bin and never handed to the shell.
        Assert.False(blob.CanOpen);
        Assert.Equal("resource.bin", blob.SuggestedFileName);
    }

    [Fact]
    public async Task A_link_carries_what_humcp_will_do_with_it()
    {
        var outcome = await Call("result_links");
        var links = outcome.Blocks.OfType<LinkResultBlock>().ToList();

        Assert.Equal(3, links.Count);
        Assert.Equal(LinkAction.Browse, links[0].Action);
        Assert.Equal("A web page", links[0].Label);
        Assert.Equal(4096, links[0].Size);

        // A file is revealed rather than opened, and a scheme humcp does not know is inert.
        Assert.Equal(LinkAction.Reveal, links[1].Action);
        Assert.Equal(LinkAction.None, links[2].Action);
        Assert.False(links[2].CanOpen);
    }

    [Fact]
    public async Task A_result_with_nothing_in_it_is_a_state_rather_than_a_failure()
    {
        var outcome = await Call("result_empty");

        Assert.False(outcome.IsError);
        Assert.Empty(outcome.Blocks);
        Assert.Null(outcome.Structured);
    }

    [Fact]
    public async Task An_awkward_structure_is_rendered_within_its_limits_and_says_so()
    {
        var outcome = await Call("result_deep");
        var node = ResultBuilder.Build(null, outcome.Structured!.Value, "Structured result");

        var rows = node.Children.Single(c => c.Path == "rows");

        Assert.Equal(2000, rows.TotalRows);
        Assert.Equal(200, rows.Rows.Count);
        Assert.True(rows.IsTruncated);

        // Four thousand characters is text, not a line in a tree.
        Assert.True(node.Children.Single(c => c.Path == "note").IsLongText);

        // And the twenty-deep branch stops somewhere that admits it stopped.
        Assert.Contains(node.SelfAndDescendants(), n => n.DegradedReason is not null);
    }
}
