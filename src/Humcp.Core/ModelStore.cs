using Humcp.Schema;

namespace Humcp.Core;

/// <summary>How much of a model is on this machine.</summary>
public enum ModelState
{
    /// <summary>Nothing has been downloaded.</summary>
    Absent,

    /// <summary>Some files, or part of one: a download stopped and can carry on from where it was.</summary>
    Partial,

    /// <summary>Every file is here, each one checked against its hash when it arrived.</summary>
    Ready,
}

/// <summary>
/// Where models live — <c>%LOCALAPPDATA%\humcp\models</c>, one folder each — and what state they are
/// in. Local rather than roaming application data: a model is gigabytes, and a roaming profile would
/// copy it to every machine the user signs in to.
/// </summary>
/// <remarks>
/// A file reaches its own name only after its hash has matched (<see cref="ModelDownloader"/>);
/// until then it is <c>.partial</c>. So "every file present at its pinned size" is the same claim as
/// "every file checked", without hashing 2.8 GB again each time humcp starts. What that does not
/// catch is a file changed in place afterwards, at the same size, by something with write access to
/// the user's own profile — which could change humcp itself as easily.
/// </remarks>
public sealed class ModelStore(string root)
{
    /// <summary>Overrides where models live. Set by tests, which must not touch a real profile.</summary>
    public const string PathVariable = "HUMCP_MODELS";

    public const string PartialSuffix = ".partial";

    public static string DefaultRoot =>
        System.Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
                "humcp", "models");

    public static ModelStore Default() => new(DefaultRoot);

    public string Root { get; } = root;

    public string FolderOf(ModelManifest model) => Path.Combine(Root, model.Id);

    public string PathOf(ModelManifest model, ModelFile file)
        => Path.Combine(FolderOf(model), file.Path.Replace('/', Path.DirectorySeparatorChar));

    public ModelState StateOf(ModelManifest model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var complete = model.Files.Count(f => SizeOf(PathOf(model, f)) == f.Size);
        if (complete == model.Files.Count) return ModelState.Ready;

        return complete > 0 || BytesOnDisk(model) > 0 ? ModelState.Partial : ModelState.Absent;
    }

    /// <summary>
    /// How much has arrived, partial files included — what a resumed download starts its count from.
    /// A finished file at the wrong size is not counted: it will be fetched again.
    /// </summary>
    public long BytesOnDisk(ModelManifest model)
    {
        ArgumentNullException.ThrowIfNull(model);

        long total = 0;
        foreach (var file in model.Files)
        {
            var path = PathOf(model, file);
            if (SizeOf(path) == file.Size) total += file.Size;
            else total += Math.Min(Math.Max(SizeOf(path + PartialSuffix), 0), file.Size);
        }

        return total;
    }

    /// <summary>Deletes the model's folder. Answers why not, or null when it is gone.</summary>
    public string? Remove(ModelManifest model)
    {
        ArgumentNullException.ThrowIfNull(model);

        try
        {
            var folder = FolderOf(model);
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Most often the model is loaded, and Windows will not delete a mapped file.
            return TextSanitizer.Clean($"The model could not be removed: {ex.Message}", TextSanitizer.HelpLength);
        }
    }

    private static long SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return -1;
        }
    }
}
