using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Core;
using Humcp.Schema;

namespace Humcp.Core.Tests;

/// <summary>
/// Resources and templates as operations (ROADMAP §11, Phase 13). Pure: no server.
/// </summary>
public class ResourceFormTests
{
    [Theory]
    [InlineData("file:///{path}", "path:required")]
    [InlineData("repo://{owner}/{name}/issues{?state,label}", "owner:required name:required state:optional label:optional")]
    [InlineData("x://{+base}{/segments*}{#frag}", "base:required segments:required frag:required")]
    [InlineData("x://{id:3}/{id}", "id:required")]
    [InlineData("x://fixed", "")]
    [InlineData("x://{&page}", "page:optional")]
    public void A_templates_variables_are_read_from_its_pattern(string template, string expected)
    {
        var read = string.Join(" ", ResourceForm.Variables(template)
            .Select(v => $"{v.Name}:{(v.Required ? "required" : "optional")}"));

        Assert.Equal(expected, read);
    }

    [Fact]
    public void A_template_is_a_form_of_its_variables()
    {
        var template = ResourceForm.Template("repo://{owner}/{name}{?state}", "issues", "Issues", "Issues of a repository.", "application/json");

        Assert.Equal(OperationKind.ResourceTemplate, template.Kind);
        Assert.Equal("repo://{owner}/{name}{?state}", template.ResourceUri);
        Assert.Equal(["owner", "name", "state"], template.Form.Fields.Select(f => f.Name));
        Assert.Equal(2, template.RequiredParameterCount);
        Assert.Equal("Issues, template, 2 required arguments", template.AccessibleName);
        Assert.Equal("Read resource", template.RunVerb);
    }

    [Fact]
    public void A_templates_variables_say_what_they_are_for()
    {
        // Found by ear (phase13-notes): a variable has no description in the protocol, so its field
        // said only how to open its drop-down.
        var only = ResourceForm.Template("demo://text/{resourceId}", "t", null,
            "Plaintext fabricated from the variable, which must be an integer. More.", null);
        Assert.Equal("Plaintext fabricated from the variable, which must be an integer.", only.Form.Fields[0].HelpText);

        var several = ResourceForm.Template("repo://{owner}/{name}", "r", null, "Issues.", null);
        Assert.All(several.Form.Fields, f => Assert.Equal("Part of the address repo://{owner}/{name}.", f.HelpText));
    }

    [Fact]
    public void A_resource_has_nothing_to_fill_in_and_says_what_it_is()
    {
        var resource = ResourceForm.Resource("file:///readme.md", "readme.md", null, null, "text/markdown");

        Assert.Equal(0, resource.ParameterCount);
        Assert.True(resource.RunsWithoutArguments);
        Assert.Equal("readme.md, text/markdown", resource.AccessibleName);
        Assert.Equal("Reading", resource.Doing);
    }

    [Fact]
    public void A_nameless_resource_is_called_by_its_address()
        => Assert.StartsWith("x://somewhere", ResourceForm.Resource("x://somewhere", null, null, null, null).DisplayName,
            StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("""{"type":"object","properties":{"uri":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"resourceUri":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"resource_uri":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"target":{"type":"string","format":"uri"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"url":{"type":"string"}}}""", false)]
    [InlineData("""{"type":"object","properties":{"during":{"type":"string"}}}""", false)]
    [InlineData("""{"type":"object","properties":{"uri":{"type":"integer"}}}""", false)]
    public void A_parameter_wants_a_resource_only_when_it_wants_an_address(string schema, bool wants)
    {
        var field = FormBuilder.Build(JsonDocument.Parse(schema).RootElement).Fields.Single();

        Assert.Equal(wants, ResourceForm.WantsResourceUri(field));
    }

    [Fact]
    public void Resources_are_a_category_of_their_own_and_never_grouped()
    {
        var snapshot = new ServerSnapshot("test", ServerConnectionState.Connected, [], "2026-07-28")
        {
            Resources =
            [
                ResourceForm.Template("file:///{path}", "file", "Any file", null, null),
                ResourceForm.Resource("file:///b", "b", null, null, null),
                ResourceForm.Resource("file:///a", "a", null, null, null),
            ],
        };

        var server = ToolTreeBuilder.BuildServerNode(snapshot);
        var category = Assert.Single(server.Children);

        Assert.Equal("Resources, 3 items", category.AccessibleName);
        Assert.All(category.Children, c => Assert.Equal(CatalogueNodeKind.Resource, c.Kind));

        // Fixed addresses by name, then the templates.
        Assert.Equal(["a", "b", "Any file, template, 1 required argument"], category.Children.Select(c => c.AccessibleName));
        Assert.Equal("test, connected, no tools, 3 resources, protocol 2026-07-28", server.AccessibleName);
    }
}

/// <summary>Resources over a real pipe: listing, reading, a template, and completion.</summary>
public class ResourceRoundTripTests(TestServerFixture fixture) : IClassFixture<TestServerFixture>
{
    private ServerConnection Connection => fixture.Connection;

    private ToolInfo Resource(string uri)
        => Connection.Resources.SingleOrDefault(r => r.ResourceUri == uri)
           ?? throw new InvalidOperationException(
               $"The fixture published no resource at {uri}. It published: " +
               string.Join(", ", Connection.Resources.Select(r => r.ResourceUri)));

    [Fact]
    public void Lists_resources_and_templates()
    {
        Assert.Equal(4, Connection.Resources.Count);
        Assert.Equal(3, Connection.Resources.Count(r => r.Kind == OperationKind.Resource));

        var template = Assert.Single(Connection.Resources, r => r.Kind == OperationKind.ResourceTemplate);
        Assert.Equal("fixture://notes/{name}", template.ResourceUri);
        Assert.Equal(["name"], template.Form.Fields.Select(f => f.Name));
        Assert.Equal("text/plain", Resource("fixture://notes/readme").MimeType);
    }

    [Fact]
    public async Task Reading_a_text_resource_keeps_its_lines()
    {
        var outcome = await Connection.CallAsync(Resource("fixture://notes/readme"), TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);
        var block = Assert.IsType<ResourceTextResultBlock>(Assert.Single(outcome.Blocks));

        Assert.False(block.IsEmbedded);
        Assert.Equal("Resource, fixture://notes/readme", block.Heading);
        Assert.Equal(3, block.Text.Split('\n').Length);
    }

    [Fact]
    public async Task A_picture_resource_is_a_picture()
    {
        var outcome = await Connection.CallAsync(Resource("fixture://pictures/square"), TestContext.Current.CancellationToken);

        var block = Assert.IsType<MediaResultBlock>(Assert.Single(outcome.Blocks));
        Assert.Equal(MediaKind.Image, block.Kind);
        Assert.Equal("image/png", block.MimeType);
    }

    [Fact]
    public async Task A_template_is_read_with_the_address_its_answers_make()
    {
        var template = Assert.Single(Connection.Resources, r => r.Kind == OperationKind.ResourceTemplate);

        var outcome = await Connection.CallAsync(template, new JsonObject { ["name"] = "shopping" },
            TestContext.Current.CancellationToken);

        Assert.False(outcome.IsError, outcome.CombinedText);
        var block = Assert.IsType<ResourceTextResultBlock>(Assert.Single(outcome.Blocks));
        Assert.Contains("Braille paper", block.Text);
    }

    [Fact]
    public async Task The_server_suggests_a_templates_values()
    {
        var template = Assert.Single(Connection.Resources, r => r.Kind == OperationKind.ResourceTemplate);

        Assert.Equal(["readme", "shopping", "todo"],
            await Connection.CompleteAsync(template, "name", "", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(["shopping"],
            await Connection.CompleteAsync(template, "name", "sh", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_linked_address_is_read_whether_or_not_it_was_listed()
    {
        // Listed: the link opens the listed resource, with its name.
        Assert.Equal("Read me", Connection.LinkedResource("fixture://notes/readme").DisplayName);

        // Not listed, but a template matches it: the server is the authority, so it is asked.
        var unlisted = Connection.LinkedResource("fixture://notes/todo");
        var outcome = await Connection.CallAsync(unlisted, TestContext.Current.CancellationToken);

        Assert.Contains("Phase 13", Assert.IsType<ResourceTextResultBlock>(Assert.Single(outcome.Blocks)).Text);
    }

    [Fact]
    public async Task An_address_the_server_does_not_have_is_a_sentence()
    {
        var outcome = await Connection.CallAsync(Connection.LinkedResource("ms-settings:windowsupdate"),
            TestContext.Current.CancellationToken);

        Assert.True(outcome.IsError);
        Assert.Contains("could not be read", outcome.CombinedText);
    }
}
