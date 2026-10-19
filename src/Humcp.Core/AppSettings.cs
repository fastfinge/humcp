using System.Text.Json;
using System.Text.Json.Nodes;
using Humcp.Schema;

namespace Humcp.Core;

/// <summary>
/// humcp's own preferences, in <c>settings.json</c> beside <c>servers.json</c> — kept apart from the
/// server list so that file stays the plain <c>mcpServers</c> block other clients read (§8).
/// </summary>
/// <remarks>
/// Edited through a form generated from <see cref="Model"/>, for the reason <see cref="ServerForm"/>
/// gives: humcp's own settings should get the accessibility contract its generated forms already
/// honour, rather than a second, hand-built one that a screen reader has never been pointed at.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>Overrides where the file lives. Set by the UI suite, which must not read a real profile.</summary>
    public const string PathVariable = "HUMCP_SETTINGS";

    public const string ShowOptionalField = "showOptionalFields";
    public const string RememberHistoryField = "rememberHistory";
    public const string SpeakInBackgroundField = "speakInBackground";

    private static readonly JsonSerializerOptions Writing = new() { WriteIndented = true };

    private readonly string? _path;

    private AppSettings(string? path) => _path = path;

    /// <summary>
    /// Whether a tool form opens with its optional parameters showing. Off by default: on a
    /// method-dispatch tool most of them do not apply, and arrowing through them is the cost (§4,
    /// pitfall 4).
    /// </summary>
    public bool ShowOptionalFields { get; set; }

    /// <summary>Whether what is typed into forms is remembered and offered again (§4, pitfall 3).</summary>
    public bool RememberHistory { get; set; } = true;

    /// <summary>
    /// Whether something that happens while humcp is not the foreground window is spoken through the
    /// screen reader directly — otherwise a call finishing in a window nobody is looking at is
    /// silent, because a live region is read only in the window with the focus. On by default, the
    /// developer's call at the Phase 6 exit (ROADMAP §13); a Settings check box turns it off.
    /// </summary>
    public bool SpeakInBackground { get; set; } = true;

    /// <summary>
    /// The width of the servers pane in device-independent pixels, when somebody set one on purpose.
    /// Null — the default — means the pane is fitted to what is in it (<see cref="PaneLayout"/>).
    /// </summary>
    public double? TreePaneWidth { get; set; }

    /// <summary>Why the last read or write did not happen, for the diagnostics window.</summary>
    public string? LastError { get; private set; }

    public static string DefaultPath =>
        System.Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } overridden
            ? overridden
            : Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
                "humcp", "settings.json");

    /// <summary>Settings that are never written anywhere, for tests.</summary>
    public static AppSettings InMemory() => new(null);

    /// <summary>Reads the file if it is there. Never throws: unreadable settings are the defaults.</summary>
    public static AppSettings Load(string? path = null)
    {
        var settings = new AppSettings(path ?? DefaultPath);

        try
        {
            if (!File.Exists(settings._path)) return settings;

            if (JsonNode.Parse(File.ReadAllText(settings._path!)) is not JsonObject root) return settings;

            settings.ShowOptionalFields = Boolean(root[ShowOptionalField]) ?? settings.ShowOptionalFields;
            settings.RememberHistory = Boolean(root[RememberHistoryField]) ?? settings.RememberHistory;
            settings.SpeakInBackground = Boolean(root[SpeakInBackgroundField]) ?? settings.SpeakInBackground;

            if (root["treePaneWidth"] is JsonValue width && width.TryGetValue(out double pixels)
                && pixels is > 0 and < 10_000)
            {
                settings.TreePaneWidth = pixels;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            settings.LastError = TextSanitizer.Clean(ex.Message, TextSanitizer.HelpLength);
        }

        return settings;
    }

    public bool Save()
    {
        if (_path is null) return true;

        try
        {
            var root = new JsonObject
            {
                [ShowOptionalField] = ShowOptionalFields,
                [RememberHistoryField] = RememberHistory,
                [SpeakInBackgroundField] = SpeakInBackground,
            };

            if (TreePaneWidth is { } width) root["treePaneWidth"] = Math.Round(width);

            var directory = Path.GetDirectoryName(_path);
            if (directory is { Length: > 0 }) Directory.CreateDirectory(directory);

            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, root.ToJsonString(Writing));
            File.Move(temporary, _path, overwrite: true);

            LastError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException)
        {
            LastError = TextSanitizer.Clean($"humcp could not save its settings: {ex.Message}", TextSanitizer.HelpLength);
            return false;
        }
    }

    // ------------------------------------------------------------------ the form

    /// <summary>The settings form. Booleans only, so every field is a check box and nothing else.</summary>
    public static FormModel Model { get; } = FormBuilder.Build(JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "showOptionalFields": {
              "type": "boolean", "title": "Show optional parameters when a form opens",
              "description": "When this is off, a tool form shows only what it needs, and Control plus O shows the rest."
            },
            "rememberHistory": {
              "type": "boolean", "title": "Remember what is typed into forms",
              "description": "Offers values used before in a drop-down, and never keeps anything whose name sounds like a password."
            },
            "speakInBackground": {
              "type": "boolean", "title": "Speak when humcp is in the background",
              "description": "Says through the screen reader when a call finishes or a server changes while another program is in front."
            }
          }
        }
        """).RootElement.Clone());

    public const string PaneWidthField = "serversPaneWidth";
    public const string PaneFitField = "fitServersPane";

    /// <summary>
    /// The width of the servers pane, as a number — the keyboard's way to do what the divider does
    /// for a mouse (§7.14).
    /// </summary>
    public static FormModel PaneModel { get; } = FormBuilder.Build(JsonDocument.Parse("""
        {
          "type": "object",
          "properties": {
            "fitServersPane": {
              "type": "boolean", "title": "Fit the servers pane to what is in it",
              "description": "Makes the pane as wide as its longest line needs, up to half the window. Untick to use the width below."
            },
            "serversPaneWidth": {
              "type": "integer", "title": "Servers pane width, in percent", "minimum": 20, "maximum": 80,
              "description": "Used when fitting is off: how much of the window the servers and tools take, the result pane having the rest."
            }
          }
        }
        """).RootElement.Clone());

    public static void FillPane(FormState state, bool fit, int percent)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (state.Find(PaneFitField) is BooleanFieldState box) box.Value = fit;
        if (state.Find(PaneWidthField) is TextFieldState field) field.Text = percent.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Whether the pane is to be fitted.</summary>
    public static bool ReadPaneFit(FormState state)
        => state?.Find(PaneFitField) is BooleanFieldState { Value: true };

    /// <summary>The percentage in the box, or null when it is not a whole number from 20 to 80.</summary>
    public static int? ReadPane(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return state.Find(PaneWidthField) is TextFieldState field
               && int.TryParse(field.Text.Trim(), System.Globalization.NumberStyles.Integer,
                   System.Globalization.CultureInfo.CurrentCulture, out var percent)
               && percent is >= 20 and <= 80
            ? percent
            : null;
    }

    /// <summary>Puts the current settings in the boxes.</summary>
    public void Fill(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        Set(state, ShowOptionalField, ShowOptionalFields);
        Set(state, RememberHistoryField, RememberHistory);
        Set(state, SpeakInBackgroundField, SpeakInBackground);
    }

    /// <summary>Takes the settings from the boxes — read directly, for the reason <see cref="ServerForm"/> gives.</summary>
    public void Read(FormState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        ShowOptionalFields = Get(state, ShowOptionalField) ?? ShowOptionalFields;
        RememberHistory = Get(state, RememberHistoryField) ?? RememberHistory;
        SpeakInBackground = Get(state, SpeakInBackgroundField) ?? SpeakInBackground;
    }

    private static void Set(FormState state, string path, bool value)
    {
        if (state.Find(path) is BooleanFieldState field) field.Value = value;
    }

    private static bool? Get(FormState state, string path)
        => state.Find(path) is BooleanFieldState field ? field.Value : null;

    private static bool? Boolean(JsonNode? node) => (node as JsonValue)?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };
}
