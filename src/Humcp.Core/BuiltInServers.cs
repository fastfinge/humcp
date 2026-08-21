using System.Text;

namespace Humcp.Core;

/// <summary>
/// Phase 1's server registry: one hard-coded stdio server (ROADMAP §10), with an environment
/// override so a real third-party server can be tried by ear without a code edit. Phase 6 replaces
/// the whole file with configuration.
/// </summary>
public static class BuiltInServers
{
    /// <summary>Overrides the executable to launch, e.g. <c>npx</c>.</summary>
    public const string CommandVariable = "HUMCP_STDIO_COMMAND";

    /// <summary>Arguments for the override, quoted the way a command line would be.</summary>
    public const string ArgumentsVariable = "HUMCP_STDIO_ARGS";

    /// <summary>Optional display name for the override; defaults to the executable's file name.</summary>
    public const string NameVariable = "HUMCP_STDIO_NAME";

    /// <summary>
    /// The server the app connects to at startup. Returns the override when
    /// <see cref="CommandVariable"/> is set, otherwise the in-repo test server.
    /// </summary>
    /// <param name="baseDirectory">Where to start looking for the repository; defaults to the running app.</param>
    public static ServerDescriptor Default(string? baseDirectory = null)
        => FromEnvironment() ?? TestServer(baseDirectory);

    /// <summary>The <c>HUMCP_STDIO_*</c> override, or null when it is not set.</summary>
    public static ServerDescriptor? FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        var command = read(CommandVariable);
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = command.Trim();
        var arguments = SplitArguments(read(ArgumentsVariable));
        var name = read(NameVariable);
        if (string.IsNullOrWhiteSpace(name)) name = Path.GetFileNameWithoutExtension(command);

        return new ServerDescriptor("env-override", name, command, arguments);
    }

    /// <summary>
    /// The in-repo fixture at <c>tests/Humcp.TestServer</c>. Located by walking up from the running
    /// assembly to the repository root, because Phase 1 has nowhere else to record a path yet.
    /// </summary>
    public static ServerDescriptor TestServer(string? baseDirectory = null)
    {
        var start = baseDirectory ?? AppContext.BaseDirectory;
        var executable = LocateTestServer(start);

        if (executable is null)
        {
            // Deliberately not a valid executable: connecting fails with a message naming the
            // fixture, which is more use than a silently empty tree.
            return new ServerDescriptor(
                "humcp-test-server", "humcp test server", "humcp-test-server-not-built", []);
        }

        return executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? new ServerDescriptor("humcp-test-server", "humcp test server", "dotnet", ["exec", executable])
            : new ServerDescriptor("humcp-test-server", "humcp test server", executable, []);
    }

    /// <summary>
    /// Splits a command-line-ish string the way a shell would, honouring double quotes. Used only
    /// for the environment override, where the user types one string and means several arguments.
    /// </summary>
    public static IReadOnlyList<string> SplitArguments(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        var arguments = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in text)
        {
            switch (c)
            {
                case '"':
                    inQuotes = !inQuotes;
                    continue;
                case ' ' when !inQuotes:
                    if (current.Length > 0) { arguments.Add(current.ToString()); current.Clear(); }
                    continue;
                default:
                    current.Append(c);
                    continue;
            }
        }

        if (current.Length > 0) arguments.Add(current.ToString());
        return arguments;
    }

    private static string? LocateTestServer(string startDirectory)
    {
        var root = FindRepositoryRoot(startDirectory);
        if (root is null) return null;

        var projectOutput = Path.Combine(root, "tests", "Humcp.TestServer", "bin");
        if (!Directory.Exists(projectOutput)) return null;

        // Prefer the configuration the caller is itself running under; a Release app talking to a
        // Debug fixture is confusing when something goes wrong.
        var preferred = startDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        var candidates = Directory.EnumerateFiles(projectOutput, "Humcp.TestServer.exe", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(projectOutput, "Humcp.TestServer.dll", SearchOption.AllDirectories))
            .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}{preferred}{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        return candidates.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
               ?? candidates.FirstOrDefault();
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Humcp.slnx"))) return directory.FullName;
            directory = directory.Parent;
        }
        return null;
    }
}
