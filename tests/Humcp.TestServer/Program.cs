using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Humcp.TestServer;

/// <summary>
/// A small stdio MCP server, spoken to over a real pipe by a real client. Phase 1's hard-coded
/// server (ROADMAP §10) and the fixture the Core and UI suites drive, so it is deliberately
/// deterministic, offline, and slightly hostile — 10,000-character descriptions, control
/// characters and bidi overrides included, per the testing notes in §9.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        // stdout is the transport. Anything written to it that is not a JSON-RPC message corrupts
        // the session, so diagnostics go to stderr — which the client captures (§8's per-server
        // error reporting) rather than losing.
        Console.Error.WriteLine("humcp test server starting.");

        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in TestTools.All()) tools.Add(tool);

        var options = new McpServerOptions
        {
            ServerInfo = new Implementation
            {
                Name = "humcp-test-server",
                Title = "humcp test server",
                Version = ThisAssemblyVersion,
            },
            Capabilities = new ServerCapabilities
            {
                Tools = new ToolsCapability { ListChanged = false },
            },
            ToolCollection = tools,
            ServerInstructions =
                "A fixture server for humcp's own tests. Nothing here touches anything outside this process.",
        };

        try
        {
            await using var transport = new StdioServerTransport(options);
            await using var server = McpServer.Create(transport, options);
            await server.RunAsync();
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("humcp test server failed: " + ex);
            return 1;
        }
    }

    private static string ThisAssemblyVersion =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
