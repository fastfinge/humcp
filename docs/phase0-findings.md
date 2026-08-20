# Phase 0 — retiring the framework risk

**Question:** is WPF's accessibility adequate for a screen-reader-first, entirely generated UI, or
do we fall back to WinForms before writing real code? (ROADMAP §10.)

**Status: complete. WPF confirmed — proceed to Phase 1.** Six defects found (three by the UIA
probe, three by ear), all fixed; each one is a rule Phase 3 now inherits. §5 lists the additions
proposed to the ROADMAP's §7 contract.

Code: the spike lived in `spike/` and was deleted at sign-off, as intended. It is preserved in git
history — `git show 337f5ec` is the commit that introduced it, and `git checkout 337f5ec -- spike`
brings it back if a later phase wants to re-run it. The parts worth keeping are reproduced in §7 of
this document, so nothing here depends on that.

Environment: .NET SDK 10.0.400, `net10.0-windows`, Windows 11 (10.0.26340), NVDA running,
`Prismatoid` 0.4.0, `FlaUI.UIA3` 5.0.0.

---

## 1. The headline: the tree survives

This is what killed WinUI 3 ([microsoft-ui-xaml#10483](https://github.com/microsoft/microsoft-ui-xaml/issues/10483) —
every node announced at level 1). WPF's `TreeView` puts the full hierarchy in the UIA tree:

| Structural depth | Items | Examples |
|---|---|---|
| 1 | 3 | `github, connected, 41 tools, protocol 2026-07-28` |
| 2 | 4 | `Tools, 41 items`, `Prompts, 2 items` |
| 3 | 10 | `Issues, 3 items`, `Read file, read-only` |
| 4 | 8 | `Create issue`, `Merge pull request, destructive` |

Both shapes from §8 work: four levels deep where a server's tools are prefix-grouped, three where
they are not. Server status folded into the accessible name reads on arrow-through as intended.

**One thing to know:** WPF does not implement the UIA `Level` property (30154) on `TreeViewItem`.
Screen readers derive depth from nesting instead, which is present and correct. It means a future
FlaUI test must assert *structural nesting*, not `Level` — the probe does this already. If v4's
lazy/virtualized tree (§11, item 7) ever needs explicit `Level`/`PositionInSet`/`SizeOfSet`, that
will take a custom automation peer.

## 2. Six findings that change how Phase 3 is written

### 2.1 A generated form must not be a plain `ItemsControl`

WPF's `ItemsControl` reports itself to UIA as a **`List`**, and each generated field as a
**`DataItem` named after the bound object's `ToString()`**. Before the fix, the probe saw exactly
this:

```
List  "Tool parameters"
  DataItem  "Humcp.Spike.SpikeField"
  DataItem  "Humcp.Spike.SpikeField"
```

A screen reader user would hear "list, 8 items" and then the name of a C# class on every field.
The form is not a list, and the view-model type name is not information.

Fix (full source in §7.1): a
custom `ItemsControlAutomationPeer` reporting `Group`, plus an `ItemAutomationPeer` reporting
`Group` with an empty name, so the only named things are the actual controls:

```
Group  "Tool parameters"
  Group  ""
    Text  "Repository"
    Edit  "Repository"  help="Owner and repository name, …"  required
```

### 2.2 The container is a tab stop until you say otherwise

`Control.Focusable` defaults to `true`, so the form's `ItemsControl` — and every nested object
group — was its own tab stop. Tab landed on an unnamed group before reaching the first field.
Fixed with a `FocusableProperty.OverrideMetadata(..., false)` in the same class. Cheap to fix,
invisible on screen, and pure noise by ear.

### 2.3 §7.6 confirmed, and the magnitude is larger than expected

With 209 generated fields on the form:

| `VirtualizingStackPanel.IsVirtualizing` | `Edit` controls visible to UIA |
|---|---|
| `False` | **209** |
| `True` | **14** |

195 of 209 fields simply do not exist in the accessibility tree. The roadmap's rule was right;
this is the number to quote when someone later proposes turning virtualization on for
performance. (The tension flagged in §7.6 for v4's large-document trees is real and unresolved —
it will need `PositionInSet`/`SizeOfSet` on a custom peer.)

### 2.4 Chrome lands in the tab order: `GridSplitter`

Both `GridSplitter`s were tab stops — two dead stops on the way from the tree to the form, every
time round. Fixed with `IsTabStop="False"`, deliberately *not* `Focusable="False"`: the splitter
stays programmatically focusable, so a "resize this pane" command can hand it the arrow keys.

**Open item for Phase 6:** as it stands there is now no keyboard route to resizing a pane at all.
That needs a View-menu command that focuses a splitter, or the splitters should not be focusable
at all and pane sizes become a setting. Either is fine; silently having no route is not.

### 2.5 Chrome lands in the tab order: the menu bar

WPF puts every top-level `MenuItem` in the tab order. A Win32 menu bar is not tabbed to — <kbd>Alt</kbd>
reaches it and left/right arrow move between menus, which already worked correctly here. Fixed on
the `Menu` with `Focusable="False"`, `IsTabStop="False"` and
`KeyboardNavigation.TabNavigation="None"`; access keys are unaffected.

### 2.6 `DatePicker` is accepted, but it must advertise Alt+Down Arrow

The comparison in item 7 is settled: **the official `DatePicker` wins**, and Phase 3 renders
`format: date` with it. One condition — the calendar opens with <kbd>Alt</kbd>+<kbd>Down Arrow</kbd>
and *nothing announces that*: not the framework, not the screen reader. A user who does not already
know the keystroke has no way to discover it.

So the renderer appends the hint to the field's help text (`AppendHintConverter`, §7.2):

```
Updated since → help="Only pull requests updated on or after this date.
                      Press Alt plus Down Arrow to open the calendar."
```

Two things this pins down for Phase 3:

- **The hint belongs to the control choice, not the schema.** The schema says `format: date`; it
  has no idea it became a `DatePicker`. A converter in the template is the right seam, so the hint
  travels with whatever control the tier decision picks.
- **`AutomationProperties.HelpText` set on a `DatePicker` reaches the inner `Edit`** that focus
  actually lands on — verified in the UIA tree, not assumed. Worth re-checking for any other
  composite control before relying on it.

"plus" is spelled out because the string is spoken and screen readers differ on whether they
announce "+" at all, depending on the user's punctuation level.

The first two defects came out of the manual pass, not the probe — the probe was measuring whether tab order
was *complete* and never asked whether it contained things it shouldn't. It now asserts both
directions, and the tab order is 16 clean stops.

## 3. What already works with no special effort

Verified mechanically by the probe, all nine checks green (§7.3 lists them):

- `AutomationProperties.Name`, `HelpText`, `IsRequiredForForm`, `ItemStatus` and `LabeledBy` all
  survive `DataTemplate` boundaries, including `ElementName` bindings to a `Label` inside the same
  template. Required fields are flagged without "(required)" in the name (§7.4).
- Tab order follows schema order exactly, reaches all 9 fields across the form and the nested
  group, and cycles: `Repository → State → Limit → Include drafts → Comment body → Updated since →
  Updated before → Author → Minimum comments`. It also contains nothing it shouldn't (2.4, 2.5).
- On submit with errors, `ItemStatus` carries the message on the field, the error summary list is
  in the UIA tree, and focus lands on the first invalid field (§7.8). Verified: focus after a
  failed submit was `Repository`.
- `GroupBox` is a real UIA group; the read-only multiline `TextBox` result pane exposes a caret.
- Runtime access-key allocation works — labels get unique keys assigned when the form is built.

Unnamed WPF chrome exists (title bar, status bar, `TreeViewItem` expander toggles) but none of it
is keyboard reachable, so it is noise in the tree rather than a usability problem. The FlaUI suite
in Phase 3 should whitelist it explicitly rather than pretend it is not there.

## 4. prism (`Prismatoid` 0.4.0) smoke test

Works:

- Loads and initialises in ~0 ms; native `prism.dll` resolves from the package's `runtimes/win-x64`.
- **Detects the running screen reader.** NVDA was picked as the best backend (priority 103, ahead
  of OneCore 98 and SAPI 97), with `speak`, `braille`, `output` and `stop` all supported.
- `Speak()` returns in 0 ms — it is asynchronous, so it is safe on the UI thread. `IsSpeaking`
  reports `True` immediately after. `Braille()` accepted through NVDA, so §13's braille question
  can be answered by experiment rather than guesswork.
- Availability transitions are delivered on a background thread — the spike marshals them to the
  dispatcher before touching the UI, and the real `IAnnouncer` must too.

Rough edges worth writing down (none blocking, all consistent with pitfall 7's "brand new"):

- **NVDA exposes no voices.** `backend.Voices` is empty and `CurrentVoice` is `null`. The settings
  UI must not assume a voice list exists — with a screen reader backend, rate/voice belong to the
  screen reader, not to us.
- **Native code writes to stderr directly**, bypassing `PrismLog`: `lex_add_addenda: cannot open
  lexicon file` appears when the SAPI/OneCore backends are probed. Harmless for a GUI app, but it
  means stderr is not ours alone.
- Probing absent screen readers logs `prism/delayimp: delay-load dummy handle pool exhausted after
  0 modules; 'PCTKUSR.dll' left unresolved` and similar for ZDSR, BoyPCReader. Benign — they are
  simply not installed — but it looks alarming in a log and is worth reporting upstream.
- `PrismBackendId.Uia` failed to acquire with `PRISM_ERROR_INVALID_PARAM` rather than
  `BACKEND_NOT_AVAILABLE`. Narrator detection should be treated as unproven until tested with
  Narrator actually running.

The spike's `IAnnouncer` wrapper proved out the shape `Humcp.Speech` should ship: every prism call
guarded, initialisation failure returning a `NullAnnouncer` rather than throwing, sanitisation of
anything heading for the synthesiser, and a rate-limited channel for progress chatter (pitfall 9).
Worth re-reading from history before writing the real one.

## 5. Proposed additions to the accessibility contract (§7)

Not applied to `ROADMAP.md` — these are the spec's to accept:

1. **Generated forms need a custom `ItemsControlAutomationPeer`** reporting `Group`, with item
   peers reporting `Group` and an empty name. Otherwise the form is a `List` of `DataItem`s named
   after the view-model class (finding 2.1).
2. **Set `Focusable="False"` on every generated-form container**, or the containers become tab
   stops with no name (finding 2.2).
3. **Assert tree hierarchy structurally**, not via the UIA `Level` property — WPF does not
   implement it (finding 1).
4. **Keep chrome out of the tab order.** Menu bars are reached with <kbd>Alt</kbd> and arrowed;
   splitters are reached from a command. WPF tab-stops both by default (findings 2.4, 2.5). The
   §7.7 rule "everything reachable by keyboard" needs its converse written down beside it: *and
   nothing else in the tab order*. The FlaUI suite should assert both directions.
5. **Where a control has a keystroke the screen reader does not announce, the renderer says so in
   `HelpText`** (finding 2.6). Today that is the `DatePicker`'s Alt+Down Arrow. The rule matters
   more than the instance: generated UI cannot rely on a user recognising a widget by sight and
   remembering how it opens.

---

## 6. The manual pass — done

Run by the project author with a screen reader. **Outcome: everything worked except two tab-order
defects**, both now fixed and both covered by the probe:

- two `GridSplitter` thumbs in the tab order (2.4)
- top-level menu items in the tab order; <kbd>Alt</kbd> and left/right arrows were already correct (2.5)

No double-speaking, no missing announcements, no dead ends. The checklist below is kept as the
script to re-run at each later phase exit — Phase 3 in particular, where the same form is
regenerated from real schemas.

Item 7 is resolved: the official `DatePicker` is accepted, with the Alt+Down Arrow hint added
(2.6). The plain ISO text box stays in the spike only as the comparison that settled it.

Not yet itemised: the JAWS and Narrator passes.

### NVDA 2026.1

| # | Check | Expected | Result |
|---|---|---|---|
| 1 | Arrow through the tree | Level 1–4 announced, expanded/collapsed state, correct child counts | |
| 2 | Server nodes | Status heard on arrow-through, not only on focus | |
| 3 | <kbd>Ctrl+F</kbd>, type `pull` | Tree narrows; is the match count heard from the status live region *without* prism? | |
| 4 | Tab through the whole form | Label, type, help text, "required" on the two required fields | |
| 5 | Anything heard twice? | Nothing double-spoken — the UIA/prism split holds (§7) | |
| 6 | Advanced filters group | Announced as a group on entry and exit | |
| 7 | `Updated since` vs `Updated before` | Which is less painful: `DatePicker` or plain ISO text box? **This decides Phase 3.** | |
| 8 | <kbd>Ctrl+Enter</kbd> on an empty form | Error count heard; focus lands on Repository | |
| 9 | Arrow back onto Repository | Is the `ItemStatus` error text announced? | |
| 10 | Error summary list | Arrowable; <kbd>Enter</kbd> jumps to the field | |
| 11 | "Add a field" | Heard? Compare the live region against the prism announcement — is prism actually needed? | |
| 12 | "Add 200 fields" then tab | Still navigable; field count sane | |
| 13 | View → Virtualize, then review the form | Fields vanish, confirming §7.6 by ear | |
| 14 | Result box | Arrow keys review line by line; text selectable | |
| 15 | <kbd>F6</kbd> around the panes | Pane name spoken via prism, clear and not intrusive | |
| 16 | Speech menu → braille only | Reaches the braille display, if one is connected (§13) | |
| 17 | Headings | Does NVDA expose `HeadingLevel` usefully in a desktop app, or is it inert? | |
| 18 | Access keys | Alt+letter reaches fields; any duplicate-key cycling? | |

### JAWS

Same list, plus: does forms mode behave in the generated form, and does the virtual cursor read
the result box sensibly?

### Narrator

A quick pass only — enough to know whether it is broken, and whether prism detects it (§4 flagged
`PrismBackendId.Uia` acquiring with an error above).

### Decision

- [x] **WPF confirmed** — proceed to Phase 1.
- [x] `DatePicker` accepted for `format: date`, with the §7.15 hint.
- [ ] ~~WPF rejected — switch to WinForms.~~

The two defects found by ear were WPF defaults doing the wrong thing in two lines of XAML, not
limits of the framework. The tree — the risk that actually mattered, and the one that ruled out
WinUI 3 — is sound. WinForms stays on the shelf.

---

## 7. Appendix — the code worth keeping

The spike is throwaway, but three pieces of it are answers rather than scaffolding. They are
reproduced here so this document stands on its own now that `spike/` is gone.

### 7.1 The generated-form automation peer (findings 2.1, 2.2)

Phase 3 needs this before the first `DataTemplateSelector` renders anything.

```csharp
/// A plain ItemsControl reports itself to UIA as a List and each generated field as a DataItem
/// named after the bound object's ToString(). This reports groups instead, and leaves the real
/// controls as the only named things.
public class FormItemsControl : ItemsControl
{
    static FormItemsControl()
    {
        // Control.Focusable defaults to true, which makes the container its own unnamed tab stop.
        FocusableProperty.OverrideMetadata(
            typeof(FormItemsControl), new FrameworkPropertyMetadata(false));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new FormItemsControlPeer(this);
}

internal sealed class FormItemsControlPeer : ItemsControlAutomationPeer
{
    public FormItemsControlPeer(ItemsControl owner) : base(owner) { }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedForm";

    protected override ItemAutomationPeer CreateItemAutomationPeer(object item)
        => new FormFieldItemPeer(item, this);
}

internal sealed class FormFieldItemPeer : ItemAutomationPeer
{
    public FormFieldItemPeer(object item, ItemsControlAutomationPeer parent) : base(item, parent) { }

    protected override AutomationControlType GetAutomationControlTypeCore()
        => AutomationControlType.Group;

    protected override string GetClassNameCore() => "GeneratedField";

    /// Empty on purpose: the label and the control inside carry the name.
    protected override string GetNameCore() => string.Empty;
}
```

### 7.2 The keystroke hint converter (finding 2.6)

Applied in the template, not the schema, so the hint travels with the control choice:

```xml
<local:AppendHintConverter x:Key="CalendarHint"
                           Hint="Press Alt plus Down Arrow to open the calendar." />
...
<DatePicker AutomationProperties.Name="{Binding Label}"
            AutomationProperties.HelpText="{Binding HelpText,
                                            Converter={StaticResource CalendarHint}}" />
```

```csharp
public sealed class AppendHintConverter : IValueConverter
{
    public string Hint { get; set; } = "";

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var text = (value as string ?? "").TrimEnd();
        if (text.Length == 0) return Hint;
        if (Hint.Length == 0) return text;

        var separator = text[^1] is '.' or '!' or '?' or ':' ? " " : ". ";
        return text + separator + Hint;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

### 7.3 The nine probe checks, to rebuild as `tests/Humcp.App.UiTests`

Every one of these found something or guards something that did:

1. Dump the UIA tree as a screen reader sees it — the artefact that made findings 2.1 and 2.2 obvious.
2. Tree hierarchy: count nested `TreeItem` elements per depth. **Not** the UIA `Level` property.
3. Every keyboard-focusable element has a non-empty `Name`; framework chrome whitelisted explicitly.
4. Every `Edit`/`ComboBox`/`CheckBox` has `HelpText` or `LabeledBy`.
5. Required fields expose `IsRequiredForForm`, and no field has "required" in its `Name`.
6. Tab order: reaches every field, cycles, and contains **no** `Thumb` or `MenuItem`.
7. A failed submit puts the message in `ItemStatus`, exposes an error summary list, and moves focus
   to the first invalid field.
8. Composite controls advertise their keystroke on the inner element that actually takes focus.
9. Virtualization guard: field count in the UIA tree with `IsVirtualizing` on versus off.

The probe launches the app and drives it with real keystrokes, so it must verify the app owns the
foreground before sending any — check the focused element's process id — or it types into whatever
the developer is doing. Ask me how I know.
