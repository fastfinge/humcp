# Phase 3 — form generation

**Goal:** a `DataTemplateSelector` over `FieldModel`, the full accessibility contract (§7), the
required/optional toggle, the enum-first wizard heuristic, MRU history for parameter values, and
validation surfacing (ROADMAP §10).

**Exit:** FlaUI suite green; a tool with fifteen parameters is comfortable to fill in by ear.

**Status: built, and its manual pass walked.** 902 tests green, 36 of them over the live UIA tree of
the running application. **The manual pass found nine defects** (§4) — three in its first four
minutes, six more over the rest of the checklist — and every one of them was invisible to the 886
automated assertions that existed when it started. All nine are fixed, each has a test, and each fix
was re-checked by ear afterwards.

Which is the argument for the manual pass, made for the second time in three phases and much more
loudly. Nothing mechanical was ever going to notice a combo box announcing
`ChoiceOption { Label = …` on every option, a Close button that closed nothing, or a caret thrown
out of the form by the shortcut the form itself advertises.

Two halves of §10's brief came back changed, both after asking the corpus rather than arguing:
the enum-first wizard heuristic is now a focus rule and not a reordering one (3.1), and the form
learned a rule nobody asked for about *not* sending things (3.2). A third came back changed after
the manual pass: the window model moved forward from Phase 6 (4.3).

Environment: .NET SDK 10.0.400, Windows 11 (10.0.26340), `ModelContextProtocol.Core` 2.2.0,
`JsonSchema.Net` 9.4.0, `FlaUI.UIA3` 5.0.0, xunit.v3 3.2.2. No new package references.

---

## 1. What exists

```
src/Humcp.Schema/               the editable half of the schema engine, still pure
├─ FieldState.cs                one field being filled in: text, choice, boolean, date,
│                               list, group, object list — and how each becomes JSON
├─ FormState.cs                 a whole form: visibility, submission, where the caret goes
├─ JsonSkeleton.cs              the starting text for a Tier 3 editor
└─ AccessKeys.cs                one letter per label, and never the same one twice

src/Humcp.Core/
└─ MruHistory.cs                what was typed last time, and what is never written down

src/Humcp.App/
├─ FieldTemplates.xaml          eleven DataTemplates: the whole control vocabulary
├─ FieldTemplateSelector.cs     FieldKind -> control, in one file and nowhere else
├─ FormItemsControl.cs          the container, with WPF's three defaults taken out
├─ FormCommands.cs              what a template's buttons ask the window to do
├─ ToolWindow.xaml(.cs)         one tool's form, its problems and its result
├─ FieldGroupWindow.cs          one row of a list of objects, in a window of its own
├─ WindowManager.cs             every open window, the Window menu, and Ctrl+1..9
└─ Converters.cs                access keys, keystroke hints, collapsing

tests/
├─ Humcp.TestServer/            demo_search (15 parameters), demo_wide (40)
├─ Humcp.Schema.Tests/          803 tests
├─ Humcp.Core.Tests/            63 tests
└─ Humcp.App.UiTests/           36 tests
```

The dependency rule is intact. `Humcp.Schema` gained the state layer and still targets `net10.0`,
still references only `JsonSchema.Net`, and still contains no string that names a WPF control.

### Where the state layer lives, and why it is not in the App

`FieldState` and `FormState` are view-models by shape — `INotifyPropertyChanged`, observable
collections, and WPF binds to them directly. ROADMAP §6 puts view-models in `Humcp.App`, so this is
a deliberate departure and is recorded as §6.3.

The reason is what is actually in them. What an empty box means, when a default is worth repeating
back, how the text a person typed becomes the JSON type the schema asked for, which field the caret
goes to after a failed submit, whether a value is sent at all — every one of those is a rule the
schema implies, and none of them is a pixel. Rules that can only be exercised by launching a
desktop application are rules that get exercised once. In `Humcp.Schema` they are exercised 803
times per build, including against all 213 harvested schemas at once (3.2 is a defect that test
found and no UI test would have).

What stayed in `Humcp.App` is everything that knows what a control is: the templates, the selector,
the windows, the keystrokes, the announcements.

---

## 2. What a generated form does

Pressing <kbd>Enter</kbd> on a tool with parameters opens a non-modal window (§8) with three panes
cycled by <kbd>F6</kbd>: the parameters, the problems, and the result. A tool with no parameters
still runs in place, as it did in Phase 1 — making somebody open a window with nothing on it to
press one more button is ceremony.

- **Optional parameters start hidden.** <kbd>Ctrl</kbd>+<kbd>O</kbd> or a check box shows them, and
  the count is announced rather than left to be discovered.
- **Focus starts on the first thing that has to be filled in.**
- **<kbd>Ctrl</kbd>+<kbd>Enter</kbd> runs it.** A form with problems warns once — how many, what the
  first one is, and that pressing again runs it anyway — and puts the caret on the first problem.
  Pressing again sends the call unchanged.
- **Problems appear in three places**: on the field as `ItemStatus`, under the control in words, and
  in a list that can be arrowed through, where <kbd>Enter</kbd> jumps to the field.
- **<kbd>F1</kbd>** describes whatever the caret is in — kind, tier, limits, whether empty means
  null, why a Tier 3 field is a JSON editor, the full description, and the schema.
- **The result** lands in the same window's read-only, caret-navigable box (§7.9).
- **<kbd>Escape</kbd> or <kbd>Ctrl</kbd>+<kbd>W</kbd> closes it**, and its own menu bar carries a
  Window menu listing every other window humcp has open (§4.3).

Eleven controls cover the whole vocabulary: text, text with history, multi-line text, choice,
check box, date, date and time, a list of primitives, a list of chosen values, a group, a list of
objects, and the Tier 3 JSON editor.

---

## 3. Findings that change later phases

### 3.1 The corpus killed half of the enum-first wizard heuristic

ROADMAP §4, pitfall 4 asks for two mitigations for the "method dispatch" tool pattern: hide optional
fields by default, and "if a required enum parameter exists, present it first in a wizard step".

Measured over the corpus before building either:

| | |
|---|---|
| Tools with a required enum at the top level | **13 of 213** |
| …of which already declare it as their **first** parameter | **9 of 13** |
| …of which have six or more parameters | 7 |
| Top-level parameters that are optional | **491 of 785 (62.5%)** |
| Tools with five or more optional top-level parameters | 33 |
| Largest forms | `firecrawl_scrape` 23 fields, 1 required; `kubectl_create` 23 fields, 0 required |

So the reordering would move a field on **four tools out of 213**, and on two of those four the
promoted enum is one of five required fields, where hoisting it above the repository and issue
number it belongs with reads worse rather than better. Against that it costs §7.7, which says tab
order follows schema order — a rule that exists because a generated form has no other predictable
shape. Server authors, it turns out, already put the dispatch switch first.

**What was built instead**: the optional toggle, which is where the measured benefit is — it turns
a 23-field form into a one-field form — and a focus rule, which costs nothing and helps every form:
the caret starts on the first required field. On a dispatch tool that is the enum, without any
reordering having happened.

This is the same shape as phase2-notes 3.3, and the same lesson: the corpus answers questions that
would otherwise be settled by taste, and it keeps answering "less than you think".

### 3.2 A value the user did not choose is not sent

The rule the form ended up with is one sentence, and it applies to every kind of field:

> A field is sent when the user chose its value. A box still holding what the schema put there says
> nothing. A required field is the exception, because the call has to state it.

It arrived as a defect. The first version prefilled from `default` and sent whatever was in the box,
which seemed harmless — the value being sent *is* the server's own default. The corpus test
`Nothing_in_the_corpus_sends_a_parameter_the_user_never_touched` disagreed on five real servers at
once: `install_helm_chart.createNamespace`, `kubectl_context.showCurrent`, `kubectl_create.validate`,
`node_management.ignoreDaemonsets`, `API-update-a-block.archived`. All booleans with
`"default": true`, all pinned to `true` by a form nobody had touched.

Repeating a default back is not harmless. It pins a value the server may have changed since it
wrote its schema, and it turns "I did not express an opinion" into an opinion.

The interesting case is the one that looks like an exception and is not. A check box that started
ticked because the schema said `default: true`, and has been unticked, **differs from its default**
and so sends `false` out loud — otherwise there would be no way to turn a stated default off. Same
rule, `IsRequired || value != initial`, no special case.

It also settled a question that had looked like it needed a control: 91 of the corpus's 134 booleans
are optional and state no default. A tri-state control for those — "not set / yes / no" — would put
an extra keystroke on the most common toggle in the application. Under this rule a two-state check
box is already unambiguous: unticked and untouched sends nothing, and there is no state in which
`false` is sent by accident.

### 3.3 "The first invalid field" is not the first issue the validator reports

§7.8 sends focus to the first invalid field on a failed submit. `ValidationReport` sorts its issues
shallowest-first and then alphabetically by path, which is right for reading a list of problems out
loud and wrong for deciding where to put the caret: on a form with `owner`, `repo` and `count`
missing, it nominates **Count**, because C comes before O.

The report cannot know better — it has a schema and an instance and no idea what order the fields
are in on screen. `FormState.FirstProblem` walks the fields in tab order instead. Both orders are
now used, for the two different jobs, and the test says so.

### 3.4 A window opened from a window is a *child* of it in the automation tree

Two facts about owned WPF windows, both of which cost time here and both of which will cost it again
in Phase 4 and Phase 6:

- **UI Automation reports an owned window under its owner.** A generated form is a descendant of the
  main window, so `FindFirstDescendant` from the main window reaches into every form that is open.
  Two elements sharing an automation id across those windows is a coin toss: the form's Run button
  and the main window's Run button were both `RunButton`, and a test meaning to press one pressed
  the other, ran the wrong tool with no arguments, and reported a server error. **Automation ids
  have to be unique across a process, not across a window.** The form's are now `FormRunButton`,
  `FormStatus`, `FormResultBox`.
- **`GetAllTopLevelWindows` does not return owned windows**, even though each has its own top-level
  handle. A test looking for a form there finds nothing and reports that the form never opened,
  while the form is open and announcing itself. Look among the owner's descendants.

A third, cheaper one: **wait for the tree to stop changing, not for a number of milliseconds.**
Revealing thirteen hidden parameters realises thirteen templates, and a test that reads the
automation tree too early on a loaded machine sees a control whose bindings have not run yet — which
is indistinguishable from a control with no accessible name. Settling to quiescence turned an
intermittent failure into no failures across three full runs. The same applies to a window closing
and to a button becoming enabled after a selection changes: all three were fixed points here, and
all three would otherwise fail only when somebody else is using the machine.

### 3.5 A label and its control share a name, so a search by name finds the label

§7.3 wants both a visible `Label` and a programmatic association, and §7.1 wants the control's own
name set. Done properly, that means two elements called "Query", and the label comes first in the
tree. A test that looks up a control by name gets the label — which has no `ItemStatus`, no value
pattern and no keyboard focus — and then asserts something false about it.

Filter to keyboard-focusable elements. Trivial once known, and a wasted half hour before that.

### 3.6 Refusing to send, and the one case that justifies it

ROADMAP §5 says never to block submission on client-side validation: the server is the authority and
an over-strict client makes a working tool unusable. That rule is about *disagreement* — we think
the server will refuse this. It says nothing about a request that cannot be constructed, which is
what unparseable text in a Tier 3 editor produces. There is no argument object to send.

So there is exactly one refusal in the application, it is not a validation failure, and it explains
itself (§7.18): *"Filters: this is not valid JSON, so the call was not sent"*, with the caret moved
to the box. Everything else is a warning that can be overruled by pressing Run again.

### 3.7 A `DataTemplate` is its own name scope

A list of primitives needs an entry control matching what the list holds — a text box for a list of
strings, a drop-down for a list of enum values — and the list's `Label` needs to point its `Target`
at whichever it is. That cannot be done with one template and a nested selector: a `DataTemplate`
creates a name scope, so a control chosen at runtime inside one is not reachable by `ElementName`
from outside it, and a label whose access key focuses nothing is worse than a duplicated template.

Two templates, `Field.List` and `Field.ListOfChoices`. Thirteen of the corpus's 82 lists hold a
closed set of values, so the second one earns its place.

### 3.8 Add and Remove, with no Edit

ROADMAP §5 specifies "ListBox + Add / Edit / Remove buttons" for a list of primitives. Edit was not
built, deliberately.

Editing an existing entry through the same box the next one is typed into is a **mode**: the box
stops meaning "the new one" and starts meaning "that one", and the only marker of which it is
would be a button caption changing from Add to Update. A mode a user cannot see the state of is
exactly the thing this application exists to avoid. Removing a mistyped entry and adding it again
costs one keystroke more than editing it; not being sure which state a control is in costs more.

If the manual pass says otherwise, an Edit that opens a small window — the way a row of an object
list already works — is the version to build, because a window has a name that is announced.

### 3.9 An access key is unique or it is absent

Windows cycles focus between controls sharing an access key. On a generated form of twenty-three
fields with keys assigned naively, <kbd>Alt</kbd>+<kbd>S</kbd> visits four unrelated boxes in an
order nobody can predict, which is worse than having no shortcut. `AccessKeys.Assign` takes the
first letter of the label, then the first letter of each later word, then any letter, and gives up
rather than duplicating.

Keys are assigned once over every field, hidden or not, so a key does not move when the optional
toggle does — and not over a list's entry box or an object list's rows, which are labelled by their
list or edited in a window of their own.

### 3.10 History is a file full of things people typed, so some of it is a password

MCP cannot autocomplete a tool argument: `completion/complete` takes `ref/prompt` and `ref/resource`
and there is no `ref/tool` (§4, pitfall 3). Local history is the whole of the answer, and it is a
plain-text file in the user's profile.

So it does not record everything. A parameter whose name reads like a credential — `apiToken`,
`api_key`, `password`, `auth.token`, `headers.Authorization` — is never written down, matched on
whole words after the name is broken up so that `keyword` is not `key`. Neither is a value longer
than 200 characters, or one with a line break in it, or anything in a multi-line or Tier 3 box.

That is a guess, and it is deliberately biased: guessing wrong in this direction costs a drop-down,
and guessing wrong in the other writes an API key to disk. It is not encryption and must not be
presented as any; Phase 6's settings need a way to turn it off and empty it, and `Forget()` is
already there for that.

The path is overridable with `HUMCP_HISTORY`, which is how the UI suite runs real tools from real
forms without writing anything into the profile of whoever ran it.

---

## 4. The manual pass — nine defects

Run in two sittings. The first was by the project author under NVDA, JAWS and Narrator, and got
three or four minutes in before finding three defects and a design complaint (4.1–4.3). **All three
screen readers behaved the same way and found the same things**, which is worth recording on its
own: these are not screen-reader quirks, they are the application being wrong.

The second walked the rest of the checklist under NVDA 2026.1 and found five more (4.4–4.10). It was
driven through an MCP bridge to the running screen reader — the keystrokes were real, the speech was
captured as NVDA produced it, and the transcript is what the findings below quote. That is worth
saying precisely: it is not a substitute for a human listening, because it cannot tell you that
something is *tiring*, and half of what a manual pass is for is exactly that. What it does give is a
pass that can be repeated exactly, and quoted.

**Everything in this section was found by ear and by no test.** 886 automated assertions had nothing
to say about a combo box announcing `ChoiceOption { Label = …`, a Close button that closed nothing,
or a caret thrown out of the form by the shortcut the form itself advertises. Every one of the nine
now has a test, and every fix below was re-checked by ear afterwards.

| | Found | What it cost the user |
|---|---|---|
| 4.1 | Combo box options read as C# object literals | Every drop-down in the application |
| 4.2 | `IsCancel` closes nothing on a non-modal window | Close button and <kbd>Escape</kbd> both dead |
| 4.3 | Windows read like dialogs | No menu bar, no route between windows |
| 4.4 | <kbd>Ctrl</kbd>+<kbd>O</kbd> threw the caret to the window | The optional toggle, unusable by ear |
| 4.5 | A form opening on a group left the caret on the window | Every tool whose first parameter is an object |
| 4.6 | <kbd>Alt</kbd>+<kbd>T</kbd> opened the Tool menu | Any field the assigner gave T or W |
| 4.7 | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> on a button pressed the button | Silently added a list row instead of running |
| 4.8 | <kbd>Delete</kbd> did nothing in a list | The list's own help text was a lie |
| 4.9 | `ItemStatus` is never spoken by NVDA | A field never said what was wrong with it |
| 4.10 | No first-letter navigation in the tree | Forty arrow presses on a forty-tool server |

### 4.1 A combo box read every option as `ChoiceOption { Label = …`

An items control takes an item's accessible name from the plain text of its content, and with a
bound object that means its `ToString`. `ChoiceOption` is a record, so the generated `ToString`
prints every property it has — and every option of every drop-down in the application announced
itself as a C# object literal.

This is exactly ROADMAP §7.12, one layer down. The Phase 0 spike found it on an `ItemsControl`'s
items and the fix was a custom peer; nobody thought to ask what a *combo box* would say about
objects bound into it, because `DisplayMemberPath` makes it look right on screen. It looks right on
screen. It is the same defect.

Fixed twice over, on purpose: `ChoiceOption.ToString()` and `FieldState.ToString()` now return the
label and the display text, and the templates set `AutomationProperties.Name` on the item containers
explicitly. The rule is now §7.23 — **anything bound into an items control needs a `ToString` worth
hearing, or an explicit item name** — and it is asserted in `Humcp.Schema.Tests` as well as over the
live tree.

### 4.2 `IsCancel` does nothing on a window that is not a dialog

The Close button did not close the form. Neither did <kbd>Escape</kbd>.

`Button.IsCancel` sets `DialogResult` on a window shown with `ShowDialog`, and does nothing at all
on one shown with `Show`. Every window in humcp is non-modal by design (§8), so `IsCancel` was
decoration on both the form window and the sub-form window. `TextWindow` had it right by accident,
because it was written with an explicit `ApplicationCommands.Close` binding.

There is now one close command, `WindowManager.CloseWindow`, bound on every window humcp opens
except the main one, with <kbd>Escape</kbd> and <kbd>Ctrl</kbd>+<kbd>W</kbd> on it and a Close item
in the menu. A UI test invokes the button and another presses the key.

### 4.3 The windows read like dialogs, because that is what they were

The complaint, in the author's words: *"the way the windows work isn't how I expected — they're more
like dialogues than windows"*. Correctly. A form window had no menu bar, no route to any other
window, and nothing to do but be dismissed.

§8 already describes what it should be — non-modal windows, several open at once, navigable via a
Window menu, <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd> in the keyboard map — and it was scheduled
for Phase 6 along with the rest of the shell. It has been brought forward, because the thing it was
scheduled behind is now the thing the user meets first.

The model is the one old multi-document applications used, mIRC among them, and the reasoning
survives translation to a screen reader well:

- **Every window has its own menu bar**, with a `Window` menu listing every open window, numbered,
  with the current one checked. Reached with <kbd>Alt</kbd> like any other menu, never in the tab
  order (§7.14).
- **<kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd> switches** to the nth, from anywhere.
- **Nothing but the main window appears in <kbd>Alt</kbd>+<kbd>Tab</kbd>** (`ShowInTaskbar = false`).
  A session with a dozen servers can have a great many windows open; putting all of them in the
  system switcher buries every other application on the machine. Inside humcp the same list can be
  numbered, named and checked, which is more use to a screen reader than a strip of icons anyway.

Everything humcp opens is tracked: forms, the sub-form window for a row of an object list, and the
F1 details and diagnostics windows.

### 4.4 The shortcut that reveals the fields threw the caret out of the form

<kbd>Ctrl</kbd>+<kbd>O</kbd> from the Query box announced *"Showing all 15 parameters"* and then
NVDA said *"Search documents — humcp test server window"* — the caret was on the **window**, not on
any control. `get_focus_info` confirmed it: focus was the window itself.

Showing the optional parameters rebuilds the collection the form is bound to (deliberately —
§7.6's rule works by the field genuinely not being in the tree), and that destroys the control the
user was standing in. WPF drops focus to the window and a screen reader says the title and stops,
which gives no clue that anything was revealed or where anything now is.

The toggle now remembers the field the caret was in and puts it back, or goes to the first required
field if that one has just been hidden. It is the single most-used keystroke on a generated form,
because 62.5% of real parameters are optional.

### 4.5 A form whose first field is a group left the caret nowhere

Opening `demo_tree`, whose one parameter is an object, announced the window and then stopped. Same
symptom as 4.4, different cause: the caret is sent to `FirstToFill`, that field is a group, and a
`GroupBox` is deliberately not a tab stop (§7.13) — so there was nothing to focus and the code gave
up silently.

It now settles for the group's first member, which is where the user was going anyway. NVDA reads
the group on the way in, so nothing is lost: *"Tree grouping required. A branch, which may contain
another branch. Name edit focused required."*

### 4.6 <kbd>Alt</kbd>+<kbd>T</kbd> opened the Tool menu instead of the Tags field

The access-key assigner reserves the letters the window's own commands use, so that §7.21 holds —
and the reserved list was written in the same phase as the buttons and never extended when the menu
bar arrived in 4.3. `_Tool` and `_Window` take T and W, a menu-bar mnemonic beats a control's, and
so the Tags field, which had been given T quite correctly, could not be reached by its own access
key at all.

A rule with a list attached is a rule that goes stale the moment somebody adds to the window without
reading it. Tags now gets G, and the list names all five letters and says which control each belongs
to.

### 4.7 <kbd>Ctrl</kbd>+<kbd>Enter</kbd> pressed the button the caret was on

Standing on "Add an entry to Rules" and pressing <kbd>Ctrl</kbd>+<kbd>Enter</kbd> added a second
empty row. A WPF `Button` handles Enter itself and does not look at the modifiers, so the window's
own <kbd>Ctrl</kbd>+<kbd>Enter</kbd> binding never saw the key.

The keyboard map (§7) says <kbd>Ctrl</kbd>+<kbd>Enter</kbd> submits, with no exception for where the
caret happens to be — and after adding a list entry the caret is very often on exactly one of those
buttons. It is now taken in a preview handler, before the focused control gets a chance.

### 4.8 <kbd>Delete</kbd> did nothing to a list entry, on a list that says <kbd>Delete</kbd> removes one

Tabbing into a list of entries focuses its first item without selecting it, and a one-item list
gives the arrow keys nowhere to move — so the selection stayed empty, every command that acts on
"the selected entry" stayed disabled, and <kbd>Delete</kbd> did nothing however many times it was
pressed. `get_focus_info` said `["FOCUSABLE", "SELECTABLE", "FOCUSED"]` and no `SELECTED`, which is
the whole finding in one line.

<kbd>Space</kbd> selected it and then <kbd>Delete</kbd> worked, which is the sort of thing you can
only discover by already knowing it. The list now selects what it focuses.

### 4.9 NVDA does not speak `ItemStatus`, so a field never said what was wrong with it

§7.5 asks for a validation message in two places: on the field, and in a navigable summary. The
summary is read perfectly. The field's own copy was set, correct, and **silent** — landing on an
invalid Query announced *"Query edit focused required What to search for. Q blank"* and nothing
about the problem. It is in `AutomationProperties.ItemStatus`, which the UI suite asserts on and
which NVDA does not read on focus.

That is a rule that assumed more than the reader delivers, and no amount of asserting the property
was going to notice. The message now goes into the help text as well, first, where it is spoken:
*"Query edit focused Problem: This one is required. What to search for."* `ItemStatus` stays, for
the readers that do use it.

### 4.10 The tree has no first-letter navigation

Pressing S on the tools tree did nothing at all: focus stayed where it was. Typing a letter to jump
to the next item beginning with it is part of what the platform assumes of any list-shaped control —
a list box and a combo box both have it, from `TextSearch` — and WPF's `TreeView` is the one that
does not.

This tree is the application's primary navigation and it is sized by whatever a server publishes.
On the fixture's fourteen tools it is a nuisance; on a forty-tool server it is the difference between
one keystroke and forty presses with a name read out after each. Written by hand, with the one-second
window and the repeat-to-cycle behaviour the rest of the shell has. §8's filter box is still the
better answer and is still Phase 6's.

### 4.11 Three smaller things, all about writing the tests

- **A window's title bar carries a `MenuBar` of its own** — the system menu — so "find the menu bar"
  finds that one first. Ours is named `Main menu` and is found by name.
- **A WPF `Menu` reports as `ControlType.Menu`, not `MenuBar`.** Only the system menu is a `MenuBar`.
- **Typing through the value pattern is not typing.** Setting a text box's `Text` does not raise
  `CommandManager.RequerySuggested`, so a button whose availability follows that text stays disabled
  forever — which reads exactly like a defect in the application and is a defect in the test. Type
  real keystrokes when the thing under test is a command's availability.

### 4.12 What the pass confirmed

Everything else on the checklist passed, and some of it is worth naming because it is what the phase
was for:

- **Nothing was ever heard twice**, across two sittings and three screen readers. §7's core rule
  holds.
- **The prefix groups in the tree help.** §13's open question since Phase 1, now closed.
- **A Tier 3 field explains itself**: *"Edit this value as JSON. The schema describes a schema that
  refers to itself."* — §7.18, out loud, on focus.
- **The omission rule is audible in the result.** Filling in two of fifteen parameters and running
  produced a result echoing the server's own defaults for the other thirteen.
- **The history file has no `apiToken` in it** after a run that filled one in and sent it (3.10). The
  file was read afterwards; the redaction is real, not merely intended.
- **A value typed once is offered the next time**: Query came back as a combo box whose drop-down
  held `aubergine`.

The one thing left unwalked is item 25 — using the application with NVDA closed — which needs a
sighted pass rather than this one, and JAWS and Narrator on the fixes above.

---

## 5. Decisions taken

| Decision | Why |
|---|---|
| The editable state layer lives in `Humcp.Schema` | It is rules, not pixels, and it is testable without a desktop (§1 above, now ROADMAP §6.3) |
| A tool with parameters opens a window; one without runs in place | Phase 1's behaviour is kept where it was already right |
| The form window carries its own result pane | Filling a form in and reading what it produced should not cross a window boundary. Phase 4 replaces the box, not the arrangement |
| Warn once, then send | §5 says client validation is advice. A warning with no way past it is a veto wearing a hat |
| A group is a `GroupBox` and is not collapsible | §5 says "collapsible". An expander is another tab stop and another state to announce, and the optional toggle already solves the length problem. Revisit if the manual pass wants it |
| A row of an object list opens in its own window, edited live, with no Cancel | A form inside a row inside a form cannot be kept track of by ear. Cancel would need a copy to roll back to, and "your changes were discarded" is news that arrives too late |
| Nothing is signalled by colour | Low-vision users are half the audience. A problem is announced, written in words, and set in bold |
| `HUMCP_HISTORY` overrides the history file | So the UI suite never writes into a real profile |

---

## 6. What Phase 3 deliberately does not do

Named so nobody mistakes an absence for an oversight:

- **The manual NVDA pass** (§6), which is what actually closes the phase.
- Result rendering beyond Phase 1's text box — `structuredContent`, images, embedded resources are
  Phase 4.
- MRTR and elicitation (Phase 5). Tier 1 is a strict superset of the elicitation subset, so the
  forms Phase 5 needs already render; what is missing is the round trip.
- Cancelling a call in flight (Phase 7), a window manager and <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd>
  (Phase 6), the <kbd>Applications</kbd>-key context menu of §7's keyboard map (Phase 6, with the
  menus), and the tree filter box (Phase 6).
- The menus' *contents* beyond Tool and Window — File, Edit, View, Help are Phase 6, and so are the
  settings behind them, including a way to turn MRU history off and empty it.

---

## 7. The manual pass — the rest of it, under NVDA 2026.1

The Phase 0 checklist (phase0-findings §6) is the script for a phase with a generated form in it,
which is what it was written for. This is that list with Phase 3's surface filled in. **Item 5 is
the one that matters most**: Phase 1's worst defect was everything being announced twice, and no
assertion in 902 tests can hear it.

**This list has now been walked** (§4). It is kept because it is the script to re-run — under JAWS
and Narrator, which have not seen the fixes, and at every later phase exit.

Run against `demo_search` (fifteen parameters, two required), `demo_wide` (forty), and
`demo_report` (a nested group and a recursive Tier 3 field).

| # | Check | Expected |
|---|---|---|
| 1 | <kbd>Enter</kbd> on "Search documents" | Form window announced by name; focus lands on Query; the count and the hidden-optional count are heard once |
| 2 | <kbd>Tab</kbd> through the form | Label, type, help text, and "required" on the two required fields — from the screen reader's own vocabulary, not from the name |
| 3 | Is the help text worth hearing? | First sentence, then the limits as a sentence. Not a paragraph |
| 4 | <kbd>Ctrl</kbd>+<kbd>O</kbd> | "Showing all 15 parameters", once; the new fields are reachable by Tab immediately |
| 5 | **Anything heard twice?** | Nothing double-spoken anywhere — §7's core rule |
| 6 | The Scope combo box | Options arrowable and read **by their labels**; "Not set" is there and is a way back (fixed, 4.1 — re-walk) |
| 7 | Include archived (default on) | Unticking it is an audible change, and the result proves `false` was sent |
| 8 | Tags: type a value, <kbd>Enter</kbd> | "Added to Tags. 1 entry now." The entry box is empty and still focused |
| 9 | Tags list, <kbd>Delete</kbd> | Removed, count announced, selection lands somewhere sensible |
| 10 | Levels (a list of chosen values) | The drop-down and the Add button are a pair that make sense by ear |
| 11 | Filter (a group) | Announced as a group on entry and on exit |
| 12 | Rules → Add entry | The sub-form window is announced by *its* name, not by its contents; Escape returns to the list (Escape fixed, 4.2 — re-walk) |
| 13 | Since (a date) | Is the Alt+Down Arrow hint heard, and does the calendar make sense once open? |
| 14 | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> on an empty form | Count heard; caret lands on Query; the problem is heard when arriving |
| 15 | Arrow back onto Query | Is the `ItemStatus` text announced on focus? |
| 16 | The problems list | Arrowable; <kbd>Enter</kbd> jumps to the field |
| 17 | <kbd>Ctrl</kbd>+<kbd>Enter</kbd> again | It runs. The warning was a warning |
| 18 | Fill it in properly and run | Completion heard once; <kbd>F6</kbd> reaches the result; the result reviews line by line |
| 19 | Reopen the form | Query is now a combo box; Alt+Down Arrow offers what was typed. Api token offers nothing, ever |
| 20 | <kbd>F1</kbd> on a field | Details window announced by title; contents readable |
| 21 | <kbd>Alt</kbd>+letter | Reaches the field it names. Any duplicate-key cycling? |
| 22 | `demo_wide`, show optional, review | Forty fields navigable; field counts sane; nothing vanishes |
| 23 | `demo_report` → the recursive field | The JSON editor's help text explains *why* it is a JSON editor |
| 24 | The tree, at 14 tools | §13's question: do the prefix groups help or cost two arrow presses? **Answered: they help. Closed** |
| 25 | With NVDA closed | Still fully usable |
| 26 | The Window menu, with three windows open | All three listed, numbered, current one checked; Ctrl+2 switches; nothing but the main window in Alt+Tab (4.3) |
| 27 | Close, four ways | The Close button, Escape, Ctrl+W and the Tool menu all close the window (4.2) |

Then the same under JAWS — including whether forms mode does the right thing on a generated form —
and a quick pass under Narrator. Both still outstanding from Phase 0.

Worth doing once against a real server, which is one environment variable (§6.0):

```
set HUMCP_STDIO_COMMAND=npx.cmd
set HUMCP_STDIO_ARGS=-y @modelcontextprotocol/server-everything
```

A form nobody wrote, for a tool nobody here has read, filled in by ear. That is the thesis, and it
is worth hearing rather than reading.

---

## 8. Additions to the roadmap — proposed

1. **§6.3** — the state layer lives in `Humcp.Schema`, with the reasoning from §1 above.
2. **§5** — the omission rule (3.2), and the one refusal that is not a validation failure (3.6).
   Also that a primitive list is Add and Remove, not Add, Edit and Remove (3.8).
3. **§7.20** — nothing is signalled by colour alone.
4. **§7.21** — an access key is unique or absent; a duplicate cycles focus between unrelated fields.
5. **§7.22** — the caret goes to the first invalid field *in tab order*, which is not the first issue
   the validator reports (3.3). A refinement of §7.8.
6. **§9** — an owned window is a child of its owner in the automation tree, automation ids must be
   unique across the process, and `GetAllTopLevelWindows` does not return owned windows (3.4). Plus
   the label-versus-control lookup (3.5), the system menu bar and the `Menu`-not-`MenuBar` control
   type (4.4).
7. **§4, pitfall 4** — the mitigation now reads as it was built and as it was measured (3.1).
8. **§7.23** — anything bound into an items control needs a `ToString` worth hearing, or an explicit
   item name. §7.12 with a combo box instead of an `ItemsControl` (4.1).
9. **§8** — the window model, written down as built: every window carries a Window menu,
   <kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd> switches, and nothing but the main window is in
   <kbd>Alt</kbd>+<kbd>Tab</kbd> (4.3). It moves out of Phase 6's entry in §10.
10. **§7's keyboard map** — <kbd>Escape</kbd> closes a form, and so does
    <kbd>Ctrl</kbd>+<kbd>W</kbd>; `IsCancel` is not how (4.2).
11. **§13** — the tool-grouping question is answered: the prefix groups help, and they stay (4.5).
