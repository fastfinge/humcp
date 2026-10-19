using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Humcp.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Before any server is started, so every one of them is born inside the job (ChildProcessJob).
        ChildProcessJob.Enter();

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            CrashLog.Write("An unobserved background task failed", args.Exception);
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            CrashLog.Write("humcp stopped because of an unexpected error", args.ExceptionObject as Exception);

        base.OnStartup(e);
    }

    /// <summary>
    /// The last chance, for a defect nobody found first. Written down, said in a native message box
    /// — which every screen reader reads, where a crash says nothing at all — and survived: the
    /// user's open forms are worth more than a clean stop.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var path = CrashLog.Write("An unexpected error", e.Exception);
        e.Handled = true;

        MessageBox.Show(
            "humcp ran into a problem it did not expect, and has carried on. " +
            (path is null
                ? "The details could not be saved."
                : $"The details are in {path}. Attaching that file to a bug report helps fix it."),
            "humcp", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}

/// <summary>Where unexpected errors are written down: <c>%LOCALAPPDATA%\humcp\errors.log</c>.</summary>
internal static class CrashLog
{
    private static readonly Lock Gate = new();

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "humcp", "errors.log");

    /// <summary>Appends one entry and answers where, or null when even that failed.</summary>
    public static string? Write(string what, Exception? exception)
    {
        try
        {
            var entry = new StringBuilder()
                .AppendLine(new string('=', 72))
                .AppendLine($"{DateTimeOffset.Now:O}  {what}")
                .AppendLine($"humcp {typeof(App).Assembly.GetName().Version?.ToString(3)} on {Environment.OSVersion}")
                .AppendLine(exception?.ToString() ?? "(no exception was given)")
                .ToString();

            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                File.AppendAllText(Path, entry);
            }

            return Path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
