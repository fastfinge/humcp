namespace Humcp.Core;

/// <summary>
/// What humcp is prepared to call a file, and what it is prepared to hand to the shell.
/// </summary>
/// <remarks>
/// <para>
/// <b>The extension is chosen here and never taken from the server.</b> A result's bytes and its
/// media type both arrive from a third party (ROADMAP §4, pitfall 5), and "save this and open it"
/// is the one action in the application that ends in Windows deciding what program to run. That
/// decision is made from the file's extension, so the extension has to come from a list this
/// project wrote — an allow-list of media types, mapped to the extension each one really is.
/// </para>
/// <para>
/// A type that is not on the list is still saved, as <c>.bin</c>, and simply not opened. Refusing
/// to save would lose the user their data to protect them from a risk that saving does not carry;
/// refusing to <em>launch</em> costs them one keystroke and a trip through their own file manager.
/// </para>
/// </remarks>
public static class MediaTypes
{
    private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = ".png",
        ["image/jpeg"] = ".jpg",
        ["image/gif"] = ".gif",
        ["image/bmp"] = ".bmp",
        ["image/webp"] = ".webp",
        ["image/tiff"] = ".tif",

        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/mpeg"] = ".mp3",
        ["audio/ogg"] = ".ogg",
        ["audio/flac"] = ".flac",
        ["audio/mp4"] = ".m4a",

        ["application/pdf"] = ".pdf",
        ["application/json"] = ".json",
        ["text/plain"] = ".txt",
        ["text/csv"] = ".csv",
        ["text/markdown"] = ".md",
    };

    /// <summary>
    /// Media types humcp will save but never launch, with the reason each one is here.
    /// </summary>
    /// <remarks>
    /// <c>image/svg+xml</c> is an image everywhere else in this file and a document that can carry
    /// script here, and the program Windows opens it with is a browser. Anything resembling a page
    /// is in the same position and is simply absent from <see cref="Known"/>.
    /// </remarks>
    private static readonly Dictionary<string, string> SaveOnly = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/svg+xml"] = ".svg",
    };

    /// <summary>The extension humcp will give this type, always beginning with a dot.</summary>
    public static string ExtensionFor(string? mimeType)
    {
        var type = Normalise(mimeType);

        if (Known.TryGetValue(type, out var known)) return known;
        if (SaveOnly.TryGetValue(type, out var saveOnly)) return saveOnly;

        return ".bin";
    }

    /// <summary>Whether a file of this type is one humcp will ask the shell to open.</summary>
    public static bool CanOpen(string? mimeType) => Known.ContainsKey(Normalise(mimeType));

    /// <summary>A file name to offer in a save dialog — a stem from the kind, and our extension.</summary>
    public static string SuggestedFileName(MediaKind kind, string? mimeType)
    {
        var stem = kind switch
        {
            MediaKind.Image => "image",
            MediaKind.Audio => "audio",
            _ => "resource",
        };

        return stem + ExtensionFor(mimeType);
    }

    /// <summary>
    /// What a part of a result <em>is</em>, in the absence of anywhere for a server to say what it
    /// shows: "Image, PNG, 45 KB" (ROADMAP §4, pitfall 6).
    /// </summary>
    public static string Describe(MediaKind kind, string? mimeType, long bytes)
    {
        var name = kind switch
        {
            MediaKind.Image => "Image",
            MediaKind.Audio => "Audio",
            _ => "File",
        };

        var type = Subtype(mimeType);
        var size = DescribeSize(bytes);

        return type.Length > 0 ? $"{name}, {type}, {size}" : $"{name}, {size}";
    }

    /// <summary>
    /// The same fact as <see cref="Describe"/>, as a phrase that fits inside a sentence: "an image,
    /// PNG, 70 bytes". The completion announcement is one sentence listing every part of a result,
    /// and a capitalised label dropped into the middle of it reads as a different register from the
    /// words around it — heard in the first Phase 4 pass as "1 line of text, Image, PNG, 70 bytes,
    /// a link to The MCP specification".
    /// </summary>
    public static string Phrase(MediaKind kind, string? mimeType, long bytes)
    {
        var noun = kind switch
        {
            MediaKind.Image => "an image",
            MediaKind.Audio => "a sound",
            _ => "a file",
        };

        var type = Subtype(mimeType);
        var size = DescribeSize(bytes);

        return type.Length > 0 ? $"{noun}, {type}, {size}" : $"{noun}, {size}";
    }

    /// <summary>A size a person would say out loud, rather than a number of bytes with nine digits.</summary>
    public static string DescribeSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024.0):0.#} MB",
    };

    /// <summary>
    /// The half of a media type worth hearing. "image/png" read out in full is three syllables of
    /// punctuation before the one word that carries the fact.
    /// </summary>
    private static string Subtype(string? mimeType)
    {
        var type = Normalise(mimeType);
        if (type.Length == 0) return "";

        var slash = type.IndexOf('/', StringComparison.Ordinal);
        var tail = slash >= 0 ? type[(slash + 1)..] : type;

        return tail.ToUpperInvariant();
    }

    /// <summary>Lower-cased, without the parameters a media type is allowed to carry.</summary>
    private static string Normalise(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return "";

        var semicolon = mimeType.IndexOf(';', StringComparison.Ordinal);
        var type = semicolon >= 0 ? mimeType[..semicolon] : mimeType;

        return type.Trim().ToLowerInvariant();
    }
}
