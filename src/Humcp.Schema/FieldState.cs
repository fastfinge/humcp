using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Humcp.Schema;

/// <summary>
/// One reason the arguments could not be assembled at all. Distinct from a
/// <see cref="ValidationIssue"/>, which is a disagreement about whether a value is acceptable:
/// this is the case where there is no value to disagree about, because the text in a Tier 3 editor
/// is not JSON.
/// </summary>
public sealed record ArgumentProblem(string FieldPath, string Label, string Message);

/// <summary>
/// What a form's fields currently add up to. <see cref="Arguments"/> is null exactly when
/// <see cref="Problems"/> is non-empty.
/// </summary>
/// <remarks>
/// This is the one place where the app legitimately refuses to send a call, and the distinction is
/// worth keeping sharp. ROADMAP §5 forbids blocking on a <em>validation</em> failure — the server is
/// the authority on what it will accept, and an over-strict client makes a working tool unusable.
/// It says nothing about a request that cannot be constructed, which is what unparseable JSON in a
/// raw editor produces. Refusing there is not strictness; there is nothing to send.
/// </remarks>
public sealed record ArgumentBuild(JsonObject? Arguments, IReadOnlyList<ArgumentProblem> Problems)
{
    public bool Ok => Arguments is not null;
}

/// <summary>
/// One field's editable state: what the user has put in it, what is currently wrong with it, and
/// how to turn that into JSON (ROADMAP §10, Phase 3).
/// </summary>
/// <remarks>
/// <para>
/// This lives beside <see cref="FieldModel"/> rather than in the WPF project on purpose. Everything
/// here is a rule the schema implies — what an empty box means, when a default is worth repeating
/// back, how the text "3,5" becomes a number — and those rules are testable without a desktop only
/// while nothing in this assembly knows what a <c>TextBox</c> is. The WPF side binds to these
/// objects directly and adds templates, windows and keystrokes, which is where it stops being
/// portable (§6.1).
/// </para>
/// <para>
/// <b>The omission rule, which is one sentence and applies to every kind:</b> a value the user did
/// not choose is not sent. A box prefilled from the schema's <c>default</c> and left alone is the
/// server's own default, and repeating it back pins a value the server may since have changed. A
/// required field is the exception — the call has to state it, so whatever is in it is sent.
/// </para>
/// </remarks>
public abstract class FieldState : INotifyPropertyChanged
{
    private string? _status;
    private int? _accessKeyIndex;

    protected FieldState(FieldModel field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Field = field;
    }

    public FieldModel Field { get; }

    public string Label => Field.Label;

    public string Path => Field.Path;

    public bool IsRequired => Field.IsRequired;

    public bool IsOptional => !Field.IsRequired;

    /// <summary>
    /// What the screen reader says after the label: what is currently wrong with this field, then
    /// the schema's first sentence, then the constraints as a sentence — because a limit nobody
    /// mentions is a trap for someone who cannot see the box turn red (§7.2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The problem is in here because NVDA does not speak <c>ItemStatus</c> on focus.</b> §7.5
    /// asks for the message on the field and in the error summary, and the summary is read
    /// perfectly; the field's own copy was set, correct, and silent — a manual pass landed on an
    /// invalid box and heard the label, the role, "required" and the description, and nothing about
    /// what was actually wrong with it (phase3-notes 4.9). <c>ItemStatus</c> is still set, because
    /// other readers do use it; this is what makes the rule true on the one that does not.
    /// </para>
    /// <para>
    /// It comes first because it is the news. Templates then add anything the <em>control</em>
    /// knows and the schema does not — a keystroke that nothing announces (§7.15), or the presence
    /// of earlier values to pick from.
    /// </para>
    /// </remarks>
    public string HelpText => _status is { Length: > 0 } problem
        ? (_help.Length > 0 ? $"Problem: {problem} {_help}" : $"Problem: {problem}")
        : _help;

    private readonly string _help = "";

    /// <summary>
    /// What is wrong with this field right now, as a sentence, or null. Goes into
    /// <c>AutomationProperties.ItemStatus</c>: WPF's red-border error template is invisible to a
    /// screen reader on its own (§7.5).
    /// </summary>
    public string? Status
    {
        get => _status;
        internal set
        {
            if (!Set(ref _status, value)) return;

            Raise(nameof(HasProblem));

            // The help text carries the problem, because that is the channel the reader speaks.
            Raise(nameof(HelpText));
        }
    }

    public bool HasProblem => _status is not null;

    /// <summary>
    /// Which character of <see cref="Label"/> is this field's access key, or null when the form ran
    /// out of letters. An index rather than marked-up text: the markup is WPF's spelling, and this
    /// assembly does not know WPF exists (§6.1, hedge 1).
    /// </summary>
    public int? AccessKeyIndex
    {
        get => _accessKeyIndex;
        set => Set(ref _accessKeyIndex, value);
    }

    /// <summary>Whether this field contributes a value to the call. See the omission rule above.</summary>
    public abstract bool IsSet { get; }

    /// <summary>What a list row shows and announces for this value.</summary>
    public virtual string Display => Label;

    /// <summary>
    /// The same thing <see cref="Display"/> says. An items control falls back to <c>ToString</c>
    /// for an item's accessible name, and the default one is a class name (ROADMAP §7.12).
    /// </summary>
    public override string ToString() => Display;

    /// <summary>
    /// The value as JSON. Only ever asked when <see cref="IsSet"/>; a null return means a JSON
    /// null, which is a value the schema may well permit, and not the same thing as absence.
    /// </summary>
    protected internal abstract JsonNode? Encode(List<ArgumentProblem> problems);

    /// <summary>This field and everything nested inside it, parents before children.</summary>
    public virtual IEnumerable<FieldState> SelfAndDescendants() { yield return this; }

    /// <summary>Builds the state tree for a set of fields, in schema order.</summary>
    public static IReadOnlyList<FieldState> CreateAll(IEnumerable<FieldModel> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return [.. fields.Select(Create)];
    }

    /// <summary>Builds the state for one field, choosing the shape from <see cref="FieldModel.Kind"/>.</summary>
    public static FieldState Create(FieldModel field)
    {
        ArgumentNullException.ThrowIfNull(field);

        return field.Kind switch
        {
            FieldKind.Choice => new ChoiceFieldState(field),
            FieldKind.Boolean => new BooleanFieldState(field),
            FieldKind.Date or FieldKind.DateTime => new DateFieldState(field),
            FieldKind.PrimitiveList => new ListFieldState(field),
            FieldKind.Group => new GroupFieldState(field),
            FieldKind.ObjectList => new ObjectListFieldState(field),
            _ => new TextFieldState(field),
        };
    }

    // ------------------------------------------------------------------ change notification

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        Raise(name);
        Raise(nameof(IsSet));
        return true;
    }

    protected void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));

    // ------------------------------------------------------------------ shared helpers

    /// <summary>
    /// The help sentence, composed once. Called from the derived constructors' initialisers via the
    /// base initialiser, so it is a static function rather than a virtual call.
    /// </summary>
    private protected static string ComposeHelp(FieldModel field)
    {
        var constraints = field.Constraints.Describe();

        if (field.HelpText.Length == 0) return constraints;
        if (constraints.Length == 0) return field.HelpText;

        var separator = field.HelpText[^1] is '.' or '!' or '?' or ':' ? " " : ". ";
        return field.HelpText + separator + constraints;
    }

    private protected FieldState(FieldModel field, bool composeHelp) : this(field)
    {
        if (composeHelp) _help = ComposeHelp(field);
    }
}

/// <summary>
/// Anything edited as text: <see cref="FieldKind.Text"/>, <see cref="FieldKind.MultilineText"/>,
/// the two number kinds, and <see cref="FieldKind.Raw"/>'s JSON editor.
/// </summary>
public sealed class TextFieldState : FieldState
{
    private readonly string _initial;
    private string _text;
    private IReadOnlyList<string> _history = [];

    internal TextFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        _initial = InitialText(field);
        _text = _initial;
    }

    public string Text
    {
        get => _text;
        set
        {
            if (Set(ref _text, value ?? "")) Raise(nameof(Display));
        }
    }

    /// <summary>
    /// Values the user has sent for this parameter before, newest first. Filled in by the app from
    /// its own store, because MCP has no way to ask the server (§4, pitfall 3: there is no
    /// <c>ref/tool</c> completion). Empty here is the ordinary case and means "no drop-down".
    /// </summary>
    public IReadOnlyList<string> History
    {
        get => _history;
        set
        {
            if (Set(ref _history, value ?? [])) Raise(nameof(HasHistory));
        }
    }

    public bool HasHistory => _history.Count > 0;

    /// <summary>
    /// True when the source can suggest values for this field as it is typed — a prompt argument on
    /// a server that answers <c>completion/complete</c> (Phase 12). Set before the form is shown, so
    /// the field is a drop-down from the start rather than turning into one under the user's hands;
    /// the suggestions themselves arrive in <see cref="History"/>, ahead of what was typed before.
    /// </summary>
    public bool HasServerSuggestions { get; set; }

    /// <summary>
    /// True when the drop-down holds the server's resource addresses as well as history — a tool's
    /// parameter that wants an address (Phase 13). Set before the form is shown, for the same reason.
    /// </summary>
    public bool OffersResources { get; set; }

    /// <summary>The JSON skeleton a Tier 3 editor started with, so an untouched one can be recognised.</summary>
    public string Seed => _initial;

    public override bool IsSet =>
        _text.Trim().Length > 0 ? IsRequired || _text != _initial : IsRequired && Field.AllowsNull;

    public override string Display => _text;

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems)
    {
        var text = Field.Kind == FieldKind.MultilineText ? _text : _text.Trim();

        // A required nullable field left empty is the one case where an empty box is a value rather
        // than an absence: the call has to say something, and the schema said null is something.
        if (text.Length == 0) return null;

        return Field.Kind switch
        {
            FieldKind.Raw => Json(text, problems),
            FieldKind.Integer => Number(text, whole: true),
            FieldKind.Number => Number(text, whole: false),
            _ => JsonValue.Create(text),
        };
    }

    private JsonNode? Json(string text, List<ArgumentProblem> problems)
    {
        try
        {
            return JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            problems.Add(new ArgumentProblem(Path, Label,
                $"This is not valid JSON, so the call was not sent. {TextSanitizer.Clean(ex.Message, 200)}"));
            return null;
        }
    }

    /// <summary>
    /// Text that is meant to be a number, as a number. Parsed in the user's own culture first —
    /// somebody typing into a form types the way their keyboard and their habits spell a decimal
    /// point — and in the invariant culture second, because a value pasted from a document or an
    /// API response spells it the other way.
    /// </summary>
    /// <remarks>
    /// Text that is not a number at all is sent as text, on purpose. The alternative is a second
    /// error channel saying the same thing the schema already says, and <see cref="SchemaValidator"/>
    /// answers it in a whole sentence: "This should be a whole number."
    /// </remarks>
    private static JsonNode? Number(string text, bool whole)
    {
        const NumberStyles Integers = NumberStyles.Integer | NumberStyles.AllowThousands;
        const NumberStyles Reals = NumberStyles.Float | NumberStyles.AllowThousands;

        foreach (var culture in Cultures)
        {
            if (whole && long.TryParse(text, Integers, culture, out var integer))
                return JsonValue.Create(integer);

            if (!whole && decimal.TryParse(text, Reals, culture, out var real))
                return JsonValue.Create(real);

            // Only after both cultures have been tried as the asked-for kind, so "3.5" in a whole
            // number field stays a decimal rather than being read as 35 by a culture that groups
            // with a dot.
            if (whole && decimal.TryParse(text, Reals, culture, out var wide))
                return JsonValue.Create(wide);

            if (double.TryParse(text, Reals, culture, out var wider))
                return JsonValue.Create(wider);
        }

        return JsonValue.Create(text);
    }

    private static readonly CultureInfo[] Cultures = [CultureInfo.CurrentCulture, CultureInfo.InvariantCulture];

    /// <summary>
    /// What the box holds before anyone types: the schema's <c>default</c>, or for a Tier 3 editor
    /// the skeleton ROADMAP §5 asks for — "ugly, but honest and never a dead end" is a lower bar
    /// than it needs to be when the shape can be shown.
    /// </summary>
    private static string InitialText(FieldModel field)
    {
        if (field.Kind == FieldKind.Raw)
        {
            return field.Default is { } stated
                ? JsonSkeleton.Format(stated)
                : JsonSkeleton.For(field.Schema);
        }

        return field.Default switch
        {
            { ValueKind: JsonValueKind.String } text => TextSanitizer.CleanMultiline(text.GetString()),
            { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
            { ValueKind: JsonValueKind.True } => "true",
            { ValueKind: JsonValueKind.False } => "false",
            _ => "",
        };
    }
}

/// <summary>A closed set of values: one selection, or none.</summary>
public sealed class ChoiceFieldState : FieldState
{
    /// <summary>
    /// The "no answer" entry. Always offered, including on a required field: a form is allowed to
    /// be incomplete while it is being filled in, and a combo box with no way back to empty is a
    /// trap. Compared by reference, so a schema that genuinely offers null is a different option.
    /// </summary>
    public static readonly ChoiceOption NotSet = new("Not set", default);

    private readonly ChoiceOption? _initial;
    private ChoiceOption? _selected;

    internal ChoiceFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        Options = [NotSet, .. field.Choices];
        _initial = Match(field.Default, field.Choices);
        _selected = _initial ?? NotSet;
    }

    public IReadOnlyList<ChoiceOption> Options { get; }

    public ChoiceOption? Selected
    {
        get => _selected;
        set
        {
            if (Set(ref _selected, value ?? NotSet)) Raise(nameof(Display));
        }
    }

    public override bool IsSet =>
        _selected is not null && !ReferenceEquals(_selected, NotSet)
        && (IsRequired || !ReferenceEquals(_selected, _initial));

    public override string Display => _selected?.Label ?? NotSet.Label;

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems) =>
        _selected is null || ReferenceEquals(_selected, NotSet)
            ? null
            : JsonNode.Parse(_selected.Value.GetRawText());

    private static ChoiceOption? Match(JsonElement? value, IReadOnlyList<ChoiceOption> options)
    {
        if (value is not { } stated) return null;

        var raw = stated.GetRawText();
        return options.FirstOrDefault(o => o.Value.GetRawText() == raw);
    }
}

/// <summary>A yes/no. See <see cref="IsSet"/> for the one subtlety.</summary>
public sealed class BooleanFieldState : FieldState
{
    private bool _value;

    internal BooleanFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        Initial = field.Default is { ValueKind: JsonValueKind.True };
        _value = Initial;
    }

    /// <summary>Whether the schema's <c>default</c> was true, which is what the box starts as.</summary>
    public bool Initial { get; }

    public bool Value
    {
        get => _value;
        set
        {
            if (Set(ref _value, value)) Raise(nameof(Display));
        }
    }

    /// <summary>
    /// The omission rule, unchanged: a box that still says what the schema said says nothing. Which
    /// covers the case that would otherwise need an exception — a box that started ticked because
    /// the schema's default was true, and has been unticked, differs from its default and so sends
    /// <c>false</c> out loud. Leaving it out there would let the server's own default back in.
    /// </summary>
    /// <remarks>
    /// 91 of the corpus's 134 booleans are optional and state no default. For those, an unticked box
    /// is a statement nobody made, and this is why it is not sent as one.
    /// </remarks>
    public override bool IsSet => IsRequired || _value != Initial;

    public override string Display => _value ? "Yes" : "No";

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems) => JsonValue.Create(_value);
}

/// <summary>A date, and for <see cref="FieldKind.DateTime"/> a time of day beside it.</summary>
/// <remarks>
/// No parameter in the corpus uses <c>format: date</c> or <c>date-time</c> (phase2-notes 3.4), but
/// the elicitation subset blesses both (§3), so Phase 5 will meet one. The control is here and
/// tested; the polish went elsewhere.
/// </remarks>
public sealed class DateFieldState : FieldState
{
    private DateTime? _date;
    private string _time = "";

    internal DateFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        if (field.Default is { ValueKind: JsonValueKind.String } stated
            && DateTimeOffset.TryParse(stated.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var parsed))
        {
            _date = parsed.Date;
            Initial = parsed.Date;
            if (HasTime) _time = parsed.ToString("HH:mm", CultureInfo.CurrentCulture);
        }
    }

    public bool HasTime => Field.Kind == FieldKind.DateTime;

    public DateTime? Initial { get; }

    public DateTime? Date
    {
        get => _date;
        set
        {
            if (Set(ref _date, value)) Raise(nameof(Display));
        }
    }

    /// <summary>Time of day as typed, in whatever the user's culture calls a time. Empty means midnight.</summary>
    public string Time
    {
        get => _time;
        set => Set(ref _time, value ?? "");
    }

    public override bool IsSet => _date is not null && (IsRequired || _date != Initial);

    public override string Display => _date is null ? "" : Encoded(_date.Value);

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems) =>
        _date is null ? null : JsonValue.Create(Encoded(_date.Value));

    private string Encoded(DateTime date)
    {
        if (!HasTime) return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var time = TimeSpan.TryParse(_time, CultureInfo.CurrentCulture, out var parsed)
                   || TimeSpan.TryParse(_time, CultureInfo.InvariantCulture, out parsed)
            ? parsed
            : TimeSpan.Zero;

        // The user's own offset, because the time they typed is the time on their clock. RFC 3339
        // has nowhere to say "no offset stated", so guessing UTC would be a silent change of hour.
        var moment = new DateTimeOffset(date.Date + time, TimeZoneInfo.Local.GetUtcOffset(date.Date + time));
        return moment.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}

/// <summary>A list of primitives: the entries, plus the box the next one is typed into.</summary>
/// <remarks>
/// Add and Remove, with no Edit — a deliberate narrowing of ROADMAP §5's "Add / Edit / Remove".
/// Edit means a mode: the entry box stops meaning "new" and starts meaning "this row", and a mode
/// with no visible marker is exactly the thing a user working by ear cannot check. Re-adding a
/// mistyped entry costs one keystroke more than editing it; being unable to tell which state a
/// control is in costs more than that.
/// </remarks>
public sealed class ListFieldState : FieldState
{
    private bool _touched;

    internal ListFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        Draft = Create(Entry(field));
        Draft.PropertyChanged += (_, _) => Raise(nameof(CanAdd));

        Items.CollectionChanged += (_, e) =>
        {
            foreach (var entry in e.NewItems?.OfType<FieldState>() ?? []) entry.PropertyChanged += OnEntryChanged;
            foreach (var entry in e.OldItems?.OfType<FieldState>() ?? []) entry.PropertyChanged -= OnEntryChanged;

            Raise(nameof(IsSet));
            Raise(nameof(Count));
            Raise(nameof(Display));
        };

        foreach (var entry in Seed(field)) Items.Add(entry);
    }

    /// <summary>
    /// An entry edited in place (1.1). Editing is choosing, so a list seeded from the schema's
    /// default stops being the server's own list the moment one of its entries changes — the
    /// omission rule's <c>value != initial</c>, one entry down.
    /// </summary>
    private void OnEntryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Only the value itself. A problem being found or cleared raises changes too, and a
        // validation pass is not the user choosing anything.
        if (e.PropertyName is not (nameof(TextFieldState.Text) or nameof(ChoiceFieldState.Selected)
            or nameof(BooleanFieldState.Value) or nameof(DateFieldState.Date) or nameof(DateFieldState.Time)))
            return;

        _touched = true;
        Raise(nameof(IsSet));
        Raise(nameof(Display));
    }

    /// <summary>The entries, in order. Bound to a list box, which is where they are read from.</summary>
    public ObservableCollection<FieldState> Items { get; } = [];

    /// <summary>The control the next entry is typed or chosen in.</summary>
    public FieldState Draft { get; }

    public int Count => Items.Count;

    public bool CanAdd => Draft.IsSet || Draft is TextFieldState { Text.Length: > 0 };

    /// <summary>
    /// The omission rule again: a list seeded from the schema's <c>default</c> and left alone is
    /// the server's own list, and is not sent back to it.
    /// </summary>
    public override bool IsSet => Items.Count > 0 && (IsRequired || _touched);

    public override string Display => Items.Count == 0
        ? "Empty"
        : string.Join(", ", Items.Take(3).Select(i => i.Display)) + (Items.Count > 3 ? ", …" : "");

    /// <summary>Moves whatever is in the entry box into the list. Returns false when it is empty.</summary>
    public bool AddDraft()
    {
        if (!CanAdd) return false;

        var entry = Create(Draft.Field);
        CopyValue(Draft, entry);

        _touched = true;
        Items.Add(entry);

        Clear(Draft);
        return true;
    }

    public bool Remove(FieldState entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        _touched = true;
        return Items.Remove(entry);
    }

    public override IEnumerable<FieldState> SelfAndDescendants()
    {
        yield return this;
        yield return Draft;
        foreach (var entry in Items) yield return entry;
    }

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems)
    {
        var array = new JsonArray();
        foreach (var entry in Items) array.Add(entry.Encode(problems));
        return array;
    }

    /// <summary>
    /// What one entry is, named for the list it is in. An item schema has no name of its own, so
    /// FormBuilder calls it "Item" — and an entry opened on its own to be edited (1.1) was announced
    /// as "Item edit", in a window titled "Environment variables, entry 1", with none of the list's
    /// help. Heard in the 1.1 NVDA pass.
    /// </summary>
    private static FieldModel Entry(FieldModel list)
    {
        var item = list.ItemField ?? list with { Kind = FieldKind.Text, Tier = FieldTier.Direct };

        return item with
        {
            Label = list.Label,
            HelpText = item.HelpText.Length > 0 ? item.HelpText : list.HelpText,
        };
    }

    private static IEnumerable<FieldState> Seed(FieldModel field)
    {
        if (field.Default is not { ValueKind: JsonValueKind.Array } stated) yield break;
        if (field.ItemField is null) yield break;

        var template = Entry(field);

        foreach (var value in stated.EnumerateArray())
        {
            var entry = Create(template with { Default = value });

            // A seeded entry is a value the user did not choose, so it obeys the omission rule the
            // same way everything else does: an untouched default list is not sent back.
            if (entry.IsSet || entry is TextFieldState { Text.Length: > 0 }) yield return entry;
        }
    }

    private static void CopyValue(FieldState from, FieldState to)
    {
        switch (from, to)
        {
            case (TextFieldState source, TextFieldState target): target.Text = source.Text; break;
            case (ChoiceFieldState source, ChoiceFieldState target): target.Selected = source.Selected; break;
            case (BooleanFieldState source, BooleanFieldState target): target.Value = source.Value; break;
            case (DateFieldState source, DateFieldState target):
                target.Date = source.Date;
                target.Time = source.Time;
                break;
            default: break;
        }
    }

    private static void Clear(FieldState state)
    {
        switch (state)
        {
            case TextFieldState text: text.Text = ""; break;
            case ChoiceFieldState choice: choice.Selected = ChoiceFieldState.NotSet; break;
            case BooleanFieldState flag: flag.Value = false; break;
            case DateFieldState date: date.Date = null; date.Time = ""; break;
            default: break;
        }
    }
}

/// <summary>An object: a real UIA group holding its own fields (§5, Tier 2).</summary>
public sealed class GroupFieldState : FieldState
{
    internal GroupFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        Fields = CreateAll(field.Fields);

        // A group's own summary is made of its members, and it is what a row of an object list is
        // announced as, so a member changing has to reach the row that shows it.
        foreach (var member in Fields)
        {
            member.PropertyChanged += (_, _) =>
            {
                Raise(nameof(IsSet));
                Raise(nameof(Display));
            };
        }
    }

    public IReadOnlyList<FieldState> Fields { get; }

    public override bool IsSet => Fields.Any(f => f.IsSet);

    public override string Display
    {
        get
        {
            var set = Fields.Where(f => f.IsSet).Take(3).Select(f => $"{f.Label} {f.Display}").ToList();
            return set.Count == 0 ? "Empty" : string.Join(", ", set);
        }
    }

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems) => Compose(Fields, problems);

    public override IEnumerable<FieldState> SelfAndDescendants()
    {
        yield return this;
        foreach (var node in Fields.SelectMany(f => f.SelfAndDescendants())) yield return node;
    }

    /// <summary>
    /// The set members of a group as one object. Shared with <see cref="FormState"/>, which is the
    /// same operation applied to the argument object itself.
    /// </summary>
    internal static JsonObject Compose(IEnumerable<FieldState> fields, List<ArgumentProblem> problems)
    {
        var value = new JsonObject();

        foreach (var field in fields)
        {
            if (!field.IsSet) continue;

            var encoded = field.Encode(problems);
            value[field.Field.Name] = encoded;
        }

        return value;
    }
}

/// <summary>A list of objects: rows, each of which is a whole nested form (§5, Tier 2).</summary>
public sealed class ObjectListFieldState : FieldState
{
    internal ObjectListFieldState(FieldModel field) : base(field, composeHelp: true)
    {
        Rows.CollectionChanged += (_, _) =>
        {
            Raise(nameof(IsSet));
            Raise(nameof(Count));
            Raise(nameof(Display));
        };
    }

    public ObservableCollection<GroupFieldState> Rows { get; } = [];

    public int Count => Rows.Count;

    public override bool IsSet => Rows.Count > 0;

    public override string Display => Rows.Count switch
    {
        0 => "Empty",
        1 => "1 entry",
        var count => $"{count} entries",
    };

    /// <summary>Adds an empty row, built from the same shape every other row has.</summary>
    public GroupFieldState AddRow()
    {
        // FormBuilder puts the row's own label and help text on ItemField and the row's shape on
        // Fields, so a sub-form window has something to be called other than the list's own name.
        var row = new GroupFieldState(Field.ItemField ?? Field with { Kind = FieldKind.Group });

        Rows.Add(row);
        return row;
    }

    public bool Remove(GroupFieldState row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return Rows.Remove(row);
    }

    public override IEnumerable<FieldState> SelfAndDescendants()
    {
        yield return this;
        foreach (var node in Rows.SelectMany(r => r.SelfAndDescendants())) yield return node;
    }

    protected internal override JsonNode? Encode(List<ArgumentProblem> problems)
    {
        var array = new JsonArray();
        foreach (var row in Rows) array.Add(row.Encode(problems));
        return array;
    }
}
