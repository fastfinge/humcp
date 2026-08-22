using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Humcp.Schema;

/// <summary>
/// One attempt at submitting a form: what it built, and what checking it found.
/// </summary>
/// <remarks>
/// The two are deliberately separate answers. <see cref="Build"/> failing means there is no request
/// to send; <see cref="Report"/> having issues means there is one and we suspect the server will
/// refuse it — which is advice, never a veto (ROADMAP §5).
/// </remarks>
public sealed record FormSubmission(ArgumentBuild Build, ValidationReport Report)
{
    /// <summary>True when a call could be made, whatever the report says about it.</summary>
    public bool CanSend => Build.Ok;

    public JsonObject Arguments => Build.Arguments ?? [];

    public bool HasIssues => Report.Issues.Count > 0;
}

/// <summary>
/// A whole form being filled in: the field states, which of them are on screen, and the one
/// operation that turns them into a call (ROADMAP §10, Phase 3).
/// </summary>
/// <remarks>
/// Pure, like everything else here, and for the same reason: the interesting parts — what an empty
/// box means, when a warning is worth stopping for, which field focus should land on — are rules
/// rather than pixels, and rules that can only be exercised by launching a desktop application are
/// rules that get exercised once.
/// </remarks>
public sealed class FormState : INotifyPropertyChanged
{
    private bool _showOptional;

    private FormState(FormModel form)
    {
        Form = form;
        Fields = FieldState.CreateAll(form.Fields);
        AssignAccessKeys();
        RefreshVisible();
    }

    public static FormState Create(FormModel form)
    {
        ArgumentNullException.ThrowIfNull(form);
        return new FormState(form);
    }

    public FormModel Form { get; }

    /// <summary>Every top-level field, in schema order, whether or not it is on screen.</summary>
    public IReadOnlyList<FieldState> Fields { get; }

    /// <summary>
    /// The fields a form actually shows. Rebuilt rather than filtered: a hidden field must be
    /// absent from the UI Automation tree, not merely invisible, or the tab order and the field
    /// count go on counting it (§7.6 is the same rule from the other direction).
    /// </summary>
    public ObservableCollection<FieldState> VisibleFields { get; } = [];

    /// <summary>
    /// Whether optional parameters are shown. Off by default, which is the measured half of §4's
    /// pitfall 4: 491 of the corpus's 785 top-level parameters are optional, and its two largest
    /// forms are 23 fields with one required and 23 with none.
    /// </summary>
    public bool ShowOptional
    {
        get => _showOptional;
        set
        {
            if (!Set(ref _showOptional, value)) return;

            RefreshVisible();
            Raise(nameof(HiddenCount));
        }
    }

    public bool HasOptional => Fields.Any(IsHideable);

    /// <summary>How many parameters the toggle is currently keeping off the screen.</summary>
    public int HiddenCount => _showOptional ? 0 : Fields.Count(IsHideable);

    public int RequiredCount => Fields.Count(f => f.IsRequired);

    /// <summary>
    /// Where focus goes when the form opens: the first thing that has to be filled in, or the first
    /// field when nothing is required.
    /// </summary>
    /// <remarks>
    /// This is what remains of §4 pitfall 4's "present the required enum first in a wizard step",
    /// and the corpus is why. Thirteen of 213 tools have a required enum at the top level, and nine
    /// of those thirteen already declare it as their first parameter — server authors put the
    /// dispatch switch first without being asked. Reordering the other four would buy very little
    /// and cost §7.7, which says tab order follows schema order. Starting on the field that decides
    /// the rest costs nothing and helps every form (phase3-notes 3.1).
    /// </remarks>
    public FieldState? FirstToFill =>
        VisibleFields.FirstOrDefault(f => f.IsRequired) ?? VisibleFields.FirstOrDefault();

    /// <summary>
    /// Where focus goes after a submit that found something (§7.8). The first field in tab order
    /// rather than the first issue in the report: the report is sorted shallowest-first and then
    /// alphabetically, which is right for reading a list of problems out and wrong for deciding
    /// which box to put the caret in — "Count" is not where the form starts just because C comes
    /// before O.
    /// </summary>
    public FieldState? FirstProblem => AllFields().FirstOrDefault(f => f.HasProblem);

    /// <summary>Every field state, nesting included.</summary>
    public IEnumerable<FieldState> AllFields() => Fields.SelectMany(f => f.SelfAndDescendants());

    public FieldState? Find(string path) => AllFields().FirstOrDefault(f => f.Path == path);

    // ------------------------------------------------------------------ submitting

    /// <summary>
    /// Assembles the arguments and checks them, and leaves every field knowing what is wrong with
    /// it. Does not send anything: whether a form with warnings on it is sent is the caller's
    /// decision, and ROADMAP §5 says the user gets to make it.
    /// </summary>
    public FormSubmission Prepare()
    {
        var build = Build();

        var report = build.Arguments is { } arguments
            ? SchemaValidator.Validate(Form, arguments)
            : ValidationReport.Clean;

        Apply(build, report);
        return new FormSubmission(build, report);
    }

    /// <summary>The arguments as they stand, with no checking.</summary>
    public ArgumentBuild Build()
    {
        var problems = new List<ArgumentProblem>();

        // A wholly raw form is one editor holding the entire argument object, so its text is the
        // request rather than one member of it (FormModel.IsWhollyRaw).
        if (Form.IsWhollyRaw && Fields is [TextFieldState raw])
        {
            var node = raw.Encode(problems);

            if (problems.Count > 0) return new ArgumentBuild(null, problems);

            return node switch
            {
                null => new ArgumentBuild([], problems),
                JsonObject json => new ArgumentBuild(json, problems),
                _ => new ArgumentBuild(null,
                    [new ArgumentProblem(raw.Path, raw.Label,
                        "The arguments have to be a JSON object — something in curly brackets.")]),
            };
        }

        var composed = GroupFieldState.Compose(Fields, problems);
        return problems.Count > 0 ? new ArgumentBuild(null, problems) : new ArgumentBuild(composed, problems);
    }

    /// <summary>
    /// Puts each problem where the user will meet it: on the field, for
    /// <c>AutomationProperties.ItemStatus</c> (§7.5), having first made sure the field is on screen
    /// to meet.
    /// </summary>
    public void Apply(ArgumentBuild build, ValidationReport report)
    {
        ArgumentNullException.ThrowIfNull(build);
        ArgumentNullException.ThrowIfNull(report);

        var byPath = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var problem in build.Problems) Add(byPath, problem.FieldPath, problem.Message);
        foreach (var issue in report.Issues) Add(byPath, issue.FieldPath, issue.Message);

        foreach (var field in AllFields())
        {
            field.Status = byPath.TryGetValue(field.Path, out var messages)
                ? string.Join(" ", messages.Distinct(StringComparer.Ordinal))
                : null;
        }

        // A problem on a field the toggle is hiding is a dead end: §7.8 sends focus to the first
        // invalid field, and there is nothing to send it to. Showing them is the only honest
        // response, and it is announced rather than silent.
        if (!_showOptional && Fields.Any(f => IsHideable(f) && f.SelfAndDescendants().Any(n => n.HasProblem)))
            ShowOptional = true;
    }

    /// <summary>Clears every field's status, for when a form is edited after a failed submit.</summary>
    public void ClearStatuses()
    {
        foreach (var field in AllFields()) field.Status = null;
    }

    private static void Add(Dictionary<string, List<string>> byPath, string path, string message)
    {
        if (!byPath.TryGetValue(path, out var messages)) byPath[path] = messages = [];
        messages.Add(message);
    }

    // ------------------------------------------------------------------ visibility and keys

    /// <summary>
    /// Hidden by the toggle: optional, or marked deprecated. A deprecated parameter is one the
    /// server is asking nobody to use, so it goes in the same drawer even when it is required.
    /// </summary>
    private static bool IsHideable(FieldState field) => field.IsOptional || field.Field.IsDeprecated;

    private void RefreshVisible()
    {
        VisibleFields.Clear();

        foreach (var field in Fields)
            if (_showOptional || !IsHideable(field))
                VisibleFields.Add(field);

        Raise(nameof(VisibleFields));
        Raise(nameof(FirstToFill));
    }

    /// <summary>
    /// Assigned once over every field, visible or not, so a key does not move when the optional
    /// toggle does (§7.3 wants a real label with an access key on every field).
    /// </summary>
    /// <remarks>
    /// Over the fields that have a label of their own on this window, which is not all of them: a
    /// list's entry box is labelled by the list, its entries are rows rather than controls, and the
    /// rows of an object list are edited in a window that does not exist yet. Giving those a key
    /// would spend letters the visible fields need.
    /// </remarks>
    private void AssignAccessKeys()
    {
        var fields = Fields.SelectMany(Labelled).ToList();
        var indices = AccessKeys.Assign([.. fields.Select(f => f.Label)], AccessKeys.WindowCommands);

        for (var i = 0; i < fields.Count; i++) fields[i].AccessKeyIndex = indices[i];
    }

    /// <summary>A field and, where it is a group, the fields it draws inside itself.</summary>
    public static IEnumerable<FieldState> Labelled(FieldState field)
    {
        ArgumentNullException.ThrowIfNull(field);

        yield return field;

        if (field is GroupFieldState group)
            foreach (var node in group.Fields.SelectMany(Labelled))
                yield return node;
    }

    // ------------------------------------------------------------------ change notification

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T storage, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value)) return false;

        storage = value;
        Raise(name);
        return true;
    }

    private void Raise(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name ?? ""));
}
