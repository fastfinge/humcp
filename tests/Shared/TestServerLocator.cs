namespace Humcp.Tests;

/// <summary>
/// Finds the in-repo fixture server, <c>tests/Humcp.TestServer</c>, by walking up from the running
/// test assembly to the repository root. Shared as source by the Core and UI suites: the app itself
/// no longer knows the fixture exists, and since Phase 6 it is only ever reached through a
/// configuration file a test wrote (ROADMAP §10).
/// </summary>
internal static class TestServerLocator
{
    /// <summary>The command and arguments that start the fixture, or null when it has not been built.</summary>
    public static (string Command, string[] Arguments)? Locate(string startDirectory)
    {
        var root = RepositoryRoot(startDirectory);
        if (root is null) return null;

        var projectOutput = Path.Combine(root, "tests", "Humcp.TestServer", "bin");
        if (!Directory.Exists(projectOutput)) return null;

        // Prefer the configuration the caller is itself running under; a Release suite talking to
        // a Debug fixture is confusing when something goes wrong.
        var preferred = startDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase) ? "Release" : "Debug";

        var candidates = Directory.EnumerateFiles(projectOutput, "Humcp.TestServer.exe", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(projectOutput, "Humcp.TestServer.dll", SearchOption.AllDirectories))
            .OrderByDescending(path => path.Contains($"{Path.DirectorySeparatorChar}{preferred}{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(File.GetLastWriteTimeUtc)
            .ToList();

        var executable = candidates.FirstOrDefault(p => p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                         ?? candidates.FirstOrDefault();

        if (executable is null) return null;

        return executable.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            ? ("dotnet", ["exec", executable])
            : (executable, []);
    }

    public static string? RepositoryRoot(string startDirectory)
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
