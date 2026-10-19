using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json.Nodes;
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
    private static async Task<int> Main(string[] args)
    {
        // stdout is the transport. Anything written to it that is not a JSON-RPC message corrupts
        // the session, so diagnostics go to stderr — which the client captures (§8's per-server
        // error reporting) rather than losing.
        Console.Error.WriteLine("humcp test server starting.");

        var tools = new McpServerPrimitiveCollection<McpServerTool>();
        foreach (var tool in TestTools.All()) tools.Add(tool);
        ListChangeTools.Collection = tools;

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
                // True since Phase 6: tools_toggle changes the list, and the client is told.
                Tools = new ToolsCapability { ListChanged = true },
            },
            ToolCollection = tools,
            ServerInstructions =
                "A fixture server for humcp's own tests. Nothing here touches anything outside this process.",
        };

        // Every progress notification the fixture sends is noted on stderr, which the client keeps in
        // its diagnostics — so a test that receives none can say whether any were sent (issue #1).
        options.Filters.Message.OutgoingFilters.Add(next => async (context, cancellationToken) =>
        {
            if (context.JsonRpcMessage is JsonRpcNotification { Method: NotificationMethods.ProgressNotification })
                Console.Error.WriteLine("fixture sent notifications/progress");

            await next(context, cancellationToken);
        });

        // `--http <port>` serves the same tools over Streamable HTTP on loopback (Phase 7), so the
        // client's HTTP transport is tested against a real server over a real socket.
        if (args is ["--http", var portText] && int.TryParse(portText, out var port))
            return await ServeHttpAsync(options, port);

        // `--malformed` publishes one more tool and then rewrites its input schema, on the way out,
        // into one that is not an object — which the SDK refuses to build, so it has to be done to
        // the message. It is how nine usable GitLab tools became one failed server (phase2-notes 3.1).
        if (args.Contains("--malformed"))
        {
            tools.Add(McpServerTool.Create(() => "Called anyway.", new McpServerToolCreateOptions
            {
                Name = MalformedName,
                Title = "A tool with a broken schema",
                Description = "Its input schema is a string schema, which the protocol does not allow.",
            }));

            options.Filters.Message.OutgoingFilters.Add(next => async (context, cancellationToken) =>
            {
                if (context.JsonRpcMessage is JsonRpcResponse { Result: JsonObject result }
                    && result["tools"] is JsonArray listed)
                {
                    foreach (var tool in listed.OfType<JsonObject>())
                        if ((string?)tool["name"] == MalformedName)
                            tool["inputSchema"] = new JsonObject { ["type"] = "string" };
                }

                await next(context, cancellationToken);
            });
        }

        // `--exit-after <ms>` dies on its own, so the client's handling of a server that goes away
        // mid-session is tested against a process that really did (Phase 7).
        if (args is [.., "--exit-after", var delayText] && int.TryParse(delayText, out var delay))
            _ = Task.Run(async () => { await Task.Delay(delay); Environment.Exit(3); });

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

    private static async Task<int> ServeHttpAsync(McpServerOptions options, int port)
    {
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");

        builder.Services
            .AddMcpServer(configured =>
            {
                configured.ServerInfo = options.ServerInfo;
                configured.Capabilities = options.Capabilities;
                configured.ToolCollection = options.ToolCollection;
                configured.ServerInstructions = options.ServerInstructions;
            })
            .WithHttpTransport();

        var app = builder.Build();
        app.MapMcp("/mcp");

        await app.StartAsync();
        Console.Error.WriteLine($"humcp test server listening on http://127.0.0.1:{port}/mcp");
        await app.WaitForShutdownAsync();
        return 0;
    }

    public const string MalformedName = "broken_schema";

    private static string ThisAssemblyVersion =>
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
