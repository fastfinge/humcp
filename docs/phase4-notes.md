# Phase 4 — result rendering

**Goal:** `structuredContent` + `outputSchema` → a grid or a property tree. Multi-part `content`
arrays. Images as objects with save and open. `resource_link` and embedded resources. `isError`
handling (ROADMAP §10).

**Exit:** every content type has a defined, non-degenerate accessible presentation.

**Status: built, and its manual pass walked.** 1,013 tests green, 51 of them over the live UIA tree
of the running application. **The manual pass found eight defects that the automated suite had
missed** (§4), and re-checking the fixes by ear turned up a ninth (4.9). By now that is the least
surprising sentence in this repository. All nine are fixed, and each has a test.

Two of the eight were not in the new code at all. <kbd>F6</kbd> had stopped reaching the tree, and
every button in the application bound to a command was announcing that command's name where a
screen reader says the keyboard shortcut — which had been true since Phase 3 and was never heard,
because nobody had listened to a *button* rather than to a field.

The corpus settled three questions again, and the first of them decided the shape of the whole
component: **25 of its 213 tools publish an `outputSchema` at all** (§3.1). A renderer that needed
one would have had nothing to show for the other 188.

Environment: .NET SDK 10.0.400, Windows 11 (10.0.26340), `ModelContextProtocol.Core` 2.2.0,
`JsonSchema.Net` 9.4.0, `FlaUI.UIA3` 5.0.0, xunit.v3 3.2.2, NVDA 2026.1. No new package references.

---

## 1. What exists

```
src/Humcp.Schema/               the schema engine, still pure, now in both directions
├─ ResultModel.cs               ResultNode: a value taken apart — values, groups, lists, tables
└─ ResultBuilder.cs             (schema?, value) -> ResultNode, and RepeatsText

src/Humcp.Core/
├─ ToolCallOutcome.cs           one typed block per kind of content, no protocol vocabulary
├─ MediaTypes.cs                what humcp will call a file, and what it will hand to the shell
└─ LinkPolicy.cs                which links humcp will follow, and what it does with the rest

src/Humcp.App/
├─ ResultView.cs                the result pane: parts in, controls out
├─ ResultSections.cs            one part as a window needs it — heading, name, picture
├─ ResultTemplates.xaml         five templates: text, media, link, table, tree
├─ ResultTemplateSelector.cs    the mapping from a shape of data to a control, in one file
├─ ResultGrid.cs                ResultDataGrid, and a table in a window of its own
├─ ResultWindows.cs             what Enter opens: a grid, a text window, or another tree
├─ ResultCommands.cs            Save, Open, Follow, Copy, Expand
├─ Launcher.cs                  the one place that hands anything to the shell
└─ TypeAheadTreeView.cs         first-letter navigation, now shared by both trees (§7.26)

tests/Humcp.TestServer/         seven new tools, one per shape a result can have
```

The dependency rule holds unchanged: `Humcp.Schema` takes a `JsonElement` and returns a
`ResultNode`, and has never heard of MCP or of WPF. `ResultBuilder.Build(schema, value)` is §6.1's
fifth hedge spelled exactly as it was written down in Phase 0, and it cost nothing to honour.

---

## 2. What a rendered result does

A result is no longer a wall of text. It is a list of parts, and each part is whatever control that
kind of content deserves:

| What the server sent | What the user gets |
|---|---|
| `text` | a read-only, caret-navigable `TextBox` (§7.9) |
| `image` | the picture, what is known about it in words, Save, and Open |
| `audio` | the same without the picture |
| `resource_link` | the address in a box that can be reviewed and copied, and an action only where `LinkPolicy` allows one |
| embedded resource, text | text, as text |
| embedded resource, blob | a file: described, saved, and opened only if humcp knows the type |
| a part of an unknown kind | its JSON, and a sentence saying this build has no presentation for it |
| `structuredContent`, a table | a real `DataGrid` with column headers |
| `structuredContent`, anything else | a tree, one node per value, read as "label, value" |
| nothing at all | "The tool returned no content." |
| `isError` | the heading, the announcement, and the first part's help text all say so |

<kbd>Enter</kbd> anywhere in a structured result opens what the caret is on in a window of its own:
a table as a grid, a long string as text, an object or a list as another tree. <kbd>F1</kbd> writes
the whole result out — every part, the structured content as JSON, and the schema it was read
against — which is the complete version of a thing the rendered result makes easy.

---

## 3. Findings that change later phases

### 3.1 Only 25 of 213 tools say what they return

The measurement that decided the component's signature. `outputSchema` is optional, and the corpus's
servers have almost entirely skipped it:

| | count | of 213 |
|---|---|---|
| tools publishing an `outputSchema` | 25 | 11.7% |
| …of which are one property wrapping the answer | 17 | |
| …of which that property is a bare string | 13 | |

So the renderer reads the **shape from the value** and takes names and descriptions from the schema
where there is one. Written the other way round — schema first, value second — it would have
rendered nothing for 188 of 213 tools, which is the failure mode a component tested only against
`@modelcontextprotocol/server-everything` would never have shown.

The schema is not decoration where it exists. `result_weather` reads out as *"Temperature in
celsius, 22.5"* and *"Conditions, Partly cloudy. What the sky is doing."* — the server's own title
and its own description, on a client that has never heard of it. That is the thesis of §1 working
in the other direction, and it is worth as much on the way back as on the way in.

### 3.2 Structured content is usually the text again

Thirteen of the 25 output schemas are `{ "content": { "type": "string" } }`, and the server fills it
with the string it has already sent as a text block. It is not duplication for its own sake:
SEP-2106 still asks an `outputSchema` to describe an object for clients on older revisions, so a
tool whose answer is a string has nowhere else to put it.

A client that renders both shows the same words twice and, worse, *says* them twice — which is
Phase 1's defect (phase1-notes 2.7) arriving in a new place by a different route. So
`ResultBuilder.RepeatsText` compares them, the duplicate is not drawn, and the announcement says
which of the two it is showing: *"Tool call complete. 3 lines of text. The structured result repeats
the text."* The raw JSON stays one keystroke away under <kbd>F1</kbd>.

### 3.3 The wrapper of one property comes off

The same specification requirement, from the other side. Seventeen of the 25 are one property
holding the whole answer — `{ "entities": [ … ] }`, `{ "content": "…" }` — and rendering the wrapper
would put a level of nesting and a keystroke in front of nearly every structured result there is.

`ResultBuilder` descends into it and keeps its name, so what the user meets is *"Entities, table, 8
rows, 5 columns"* rather than *"Structured result, 1 field"* with a table inside it. The property's
own name says more than the wrapper ever could.

### 3.4 A cap and a virtualization rule are two halves of one decision

§7.6 says UI virtualization stays off, because a virtualized row is not in the UI Automation tree at
all. A grid honouring that is a grid that builds every row it shows — so **`MaxRows` is 200**, and
the two settings cannot be reasoned about separately: raise the cap and the grid stops being quick;
turn virtualization on to make it quick again and the rows stop being reachable.

What makes that honest rather than a quiet truncation is that the node keeps the real total. A grid
of two thousand rows announces *"Rows, table, 2,000 rows, 2 columns"* and, on focus, *"Showing the
first 200 of 2,000 rows"*. The user is told what they are not being shown (§7.18).

### 3.5 The extension is ours, and the scheme allow-list has two entries

Saving and opening a result is the one place in the application where Windows is asked to choose a
program, and both inputs to that decision — the bytes and the media type — come from a third party
(§4, pitfall 5). Two rules, both in `Humcp.Core` so they are testable without a desktop:

- **`MediaTypes`**: an allow-list of media types to extensions. A type not on it is still saved, as
  `.bin`, and never opened — refusing to save would lose the user their data to protect them from a
  risk that saving does not carry, while refusing to *launch* costs one trip through their own file
  manager. `image/svg+xml` is on the list to be saved and off it to be opened, because it is an
  image everywhere else and a scriptable document here.
- **`LinkPolicy`**: `http` and `https` go to the browser, `file:` is *revealed* in a folder rather
  than opened, and everything else is copyable and inert — with a sentence saying so, rather than a
  button that does nothing (§7.18). `ms-settings:`, `vscode://` and a `file:` URI ending in `.exe`
  are all the same attack, and the browser is the one handler whose job is to be handed untrusted
  addresses.

### 3.6 A picture is still worth drawing

The preview is decoded and shown, for the half of this application's audience that has some sight
and is using it (§7.20). It is bounded: only for a media type recognised as a raster image, and
only when the frame is under 40 megapixels, because a few hundred kilobytes of PNG can decode to
hundreds of megabytes of bitmap and the bytes are a stranger's. A picture that will not decode
costs the preview and nothing else — it is still described, saved and opened.

---

## 4. The manual pass — eight defects, and a ninth on the re-check

Driven under NVDA 2026.1, keyboard only, through the MCP screen-reader bridge in silent mode: the
reader spoke into a capture rather than into the room, and what it would have said is what is quoted
below. Every one of these was found by ear in about fifteen minutes, and the 1,007 automated tests
that were green at the time had found none of them.

### 4.1 <kbd>F6</kbd> never reached the tree

Pressing <kbd>F6</kbd> in the main window announced *"Servers and tools tree view"* — and then the
caret was in the result box, silently. Pressing it again did the same. The tree, which is this
application's primary navigation, had dropped out of its own pane cycle.

Phase 4 turned the pane list from `Action[]` into `Func<bool>[]`, so that a pane with nothing to
focus could be skipped. **`TreeView.Focus()` answers `false` once the tree has items** — focus
belongs to the selected `TreeViewItem`, which is the same fact §9 already records about focusing a
tree from a test — so the cycle read a successful move as a failure and walked on to the next pane.

The fix asks where the focus actually ended up (`Tree.IsKeyboardFocusWithin`) rather than trusting
the answer. The lesson is smaller and sharper: **a WPF `Focus()` return value is not a report about
where the caret is.**

The test that existed pressed <kbd>F6</kbd> up to twice and looked for the result pane. It passed
throughout. A cycle asserted at one end is not asserted, and the test now walks to both.

### 4.2 A grid announced a C# class name on every row

> *"ResultRow { Number = 1, Cells = System.Collections.Generic.List\`1[Humcp.Schema.ResultCell],
> Summary = entity-0001, place, 2 items, 0.143, false } data item 1 of 8"*

This is §7.23 word for word, in the third control it has now been found in — a combo box in Phase 3,
an `ItemsControl` in Phase 0, a `DataGrid` here. An item bound into a list-shaped control takes its
accessible name from the plain text of its content, and for a bound object that is `ToString()`; a
record's generated one reads out every property it has, C# type names included.

Fixed in both places the rule asks for: `ResultRow.ToString()` returns the row's values, and the
grid's `RowStyle` sets `AutomationProperties.Name` as well. Either alone is a fix somebody deletes
later without hearing what it did.

### 4.3 Two hundred rows on screen, and an arrow key that left the grid

With focus on a grid of 200 rows, pressing <kbd>Down Arrow</kbd> announced *"Window collapsed
Alt+W"* — the menu bar. Every subsequent arrow drove the menu. The rows were on screen, in the
automation tree, and unreachable.

**A `DataGrid` with no current cell does not handle an arrow key at all**, so WPF treated the press
as directional navigation and moved focus out of the control. It only bites when the grid is focused
in code — which is exactly what a window that opens onto a grid does.

This is phase3-notes 4.8 again ("a list selects what it focuses"), and the fix is the same shape:
`OnGotKeyboardFocus` gives the grid a current cell if it has none. Two controls in two phases have
now needed it, which makes it a rule (§7.29) rather than a coincidence — so the tree got it too,
where the symptom was milder and real: the first arrow press was spent arriving at the first node
rather than moving to the second.

### 4.4 A picture said what it was to nobody

Tab through a result with an image in it and the announcements were *"Text, part 1 of 6"*, *"Save
Image, PNG, 70 bytes button"*, *"Open Image, PNG, 70 bytes button"*. The part's heading and the
sentence underneath it — *"Image, PNG, 70 bytes. There is no description of it: the protocol has
nowhere to put one."* — were `TextBlock`s: on screen, in the automation tree, and in nobody's ear,
because nothing focusable carried them.

§7.24 says a message the reader does not speak has not been delivered, and it was written in Phase 3
about `ItemStatus`. It generalises: **a part of a result needs exactly one focusable control that
says what it is.** The description is now a read-only box, like the link's address beside it, so it
is reachable, reviewable with a caret, and copyable.

### 4.5 A button announced its command's name as its keyboard shortcut

> *"Save Image, PNG, 70 bytes, button, … humcp picks the extension from the media type, never from
> the server. **Save as**"*

That trailing fragment is in the slot where a screen reader says the shortcut — the same slot that
correctly reads *"C"* for Reconnect and *"U"* for Run tool. **WPF's `ButtonBaseAutomationPeer` falls
back to a bound `RoutedUICommand`'s `Text` for `AcceleratorKey` when none is set**, so every button
bound to a command claims a shortcut it does not have.

It was true of the form's Run button through the whole of Phase 3, announcing *"Run"* as its
shortcut instead of *"Control plus Enter"*, and nobody heard it because Phase 3 listened to fields.

Fixed both ways round: every `RoutedUICommand` in the application now has an empty `Text`, and the
buttons that really do have a keystroke carry it explicitly, spelled out as §7.15 requires —
"Control plus Enter" on Run, "Escape" on Close, "Delete" on Remove, "Enter" on Edit. Four keystrokes
that were previously only in help text are now in the place a reader looks for them.

### 4.6 "The revision this server speaks.. text/html"

A link's explanation is assembled from the server's description, its media type, its size and what
humcp will do with it. Joining them with `". "` doubles the stop whenever the server's own
description already ends in one — which it usually does. Now the join looks at what it is joining.

### 4.7 A nested value fell out of its tree and into JSON

Pressing <kbd>Enter</kbd> on a cell holding a list opened *"Observations — JSON"*: a text box of
brackets and quotation marks, for a value that had been a navigable tree one keystroke earlier. It
was honest and it was a step down.

Now the shape decides the window, and the same two resources build both trees, so a nested value
opens as the tree it already was. Raw JSON is still there under <kbd>F1</kbd> and Copy, which is
where an escape hatch belongs.

### 4.8 One sentence, three registers

> *"Tool call complete. 1 line of text, Image, PNG, 70 bytes, a link to The MCP specification, an
> embedded resource, 2 lines, File, X-NOT-A-REAL-TYPE, 8 bytes."*

Capitalised labels dropped into the middle of a sentence beside noun phrases. It is only cosmetic
until you remember that this announcement is the *only* part of a result most users hear without
going to look for it. Media parts now have a phrase form — "an image, PNG, 70 bytes" — for exactly
this position.

### 4.9 And one the re-check found

Every fix was re-checked by ear, which is where the ninth defect came from. Giving the tree a
selected node on focus (§4.3's rule, applied to both controls) left the caret on the tree itself
rather than on the node — so the first arrow press moved focus to the *second* node while the
selection was still catching up on the first, and NVDA said *"Note, 4,000 characters, **not
selected**"*.

Selecting and then focusing, in that order, fixes it: the caret starts on the first node, Enter
works immediately, and the first arrow moves cleanly to the second. **A fix is not finished until it
has been listened to** — which is the same lesson as the phase, one level down.

### 4.10 What the pass confirmed

Not everything was broken, and the things that worked are worth recording because they were the
riskiest parts of the design:

- A tree node reads as one thing: *"Temperature in celsius, 22.5"*, *"Rows, table, 2,000 rows, 2
  columns"*, *"Note, 4,000 characters"*. Putting the label and the value in one accessible name is
  what makes a structured result reviewable by arrowing rather than by hunting.
- The grid announces the column with every cell — *"place, Kind column 2"*, *"2 items, Observations
  column 3"* — which is the thing a grid does that no list of "name: value" lines can.
- A truncated table says so on focus, with the real total.
- <kbd>Escape</kbd> closes a window opened from a result and puts the caret back where it came from,
  including from two windows deep (§7.25 holding under a case Phase 3 did not have).
- The error path reads exactly as intended: *"Tool returned an error. This tool always fails. That
  is its entire job."*
- First-letter navigation in the tree still works, and now works in both trees, because it moved
  into a control rather than staying a handler on one window (§7.26).

---

## 5. Decisions taken

- **The result renderer takes `(schema?, value)`.** §6.1's fifth hedge, honoured exactly, and the
  corpus turned it from a portability hedge into a functional requirement (§3.1).
- **Shape decides the control, and nothing else does.** An array of objects is a grid; anything else
  structured is a tree; a string long enough to want a caret is a window of text. There is no list
  of tool names anywhere in the renderer, and no server-specific branch.
- **The parts of a result live in a `FormItemsControl`.** A result is a generated list of controls
  in exactly the sense a form is, and the three WPF defaults that make an `ItemsControl` unusable
  with a screen reader (§7.12, §7.13, §7.6) are wrong here for the same reasons.
- **A table opens as a grid in its own window** rather than being nested inside a tree inside a
  pane — the same decision as a row of an object list on the way in (§5, Tier 2), for the same
  reason: a window has a name announced on entry and a boundary obvious from either side.
- **`MaxRows` is 200**, and it is tied to virtualization staying off (§3.4).
- **`TextSanitizer.ResultLength` is 200,000**, ten times the details cap. A tool that reads a file
  answers with the file, and handing back its first twenty thousand characters as though they were
  the whole thing is the kind of silent lie this project exists to avoid. When it does bite, the
  part's heading says so: "Text, 200,000 of 431,902 characters".
- **`Humcp.App` still does not reference the MCP SDK.** Every kind of content crosses as a neutral
  block; `MediaKind`, not `ImageContentBlock`.

---

## 6. What Phase 4 deliberately does not do

- **No markdown rendering.** §7.10 rules out a web view, and converting markdown to native controls
  is a project of its own. Text arrives as text, with its line structure intact.
- **No generated alt text for images.** That is the `IResultSummarizer` seam, and it is v2 (§2). The
  honest presentation until then is the object itself, which is what pitfall 6 asks for.
- **No sorting or filtering in the grid.** A `DataGrid` will sort by a column header, and a sort that
  a screen reader user cannot tell has happened is worse than no sort. Phase 6 owns the shell, and a
  sort that announces itself belongs there.
- **No progress for long calls.** Pitfall 9's rate-limited progress into the live region is Phase 6.
- **No `Applications`-key context menu.** The keyboard map promises Copy schema, Copy result and Run
  again on it; the copy commands exist as <kbd>Control</kbd>+<kbd>Shift</kbd>+<kbd>C</kbd> and
  buttons, and the menu itself is Phase 6 shell work.

---

## 7. Additions to the roadmap — proposed

**Contract (§7):**

27. **A button bound to a `RoutedUICommand` announces that command's `Text` as its keyboard
    shortcut.** WPF's `ButtonBaseAutomationPeer` uses it as the `AcceleratorKey` fallback, so a
    command called "Save as" makes every button bound to it claim a shortcut that does not exist —
    in the same slot that correctly reads "Control plus Enter" elsewhere. Commands carry no `Text`;
    buttons that have a real keystroke set `AutomationProperties.AcceleratorKey` explicitly, spelled
    out (§7.15).
28. **Every part of a result has exactly one focusable control that says what it is.** A heading and
    an explanation in `TextBlock`s are on screen and in nobody's ear; the buttons beside them are
    named for the action, not for the thing. §7.24 generalised from a validation message to
    anything a window knows and does not say.
29. **A control that navigates with the arrow keys sets its own current position when it takes
    focus** — a list box its selection, a grid its current cell. Without it the control does not
    handle the first arrow key at all and WPF moves focus somewhere else entirely, silently. Found
    twice now, in two controls (phase3-notes 4.8, phase4-notes 4.3).
30. **A `Focus()` return value is not a report about where the caret is.** `TreeView.Focus()`
    answers false while moving focus into the tree. Ask the tree.

**Testing notes (§9):**

- **A cycle asserted at one end is not asserted.** The <kbd>F6</kbd> test looked for the result pane
  and passed for a whole phase while the tree had dropped out of the cycle.
- **A `DataGrid` refuses `SetFocus` through UI Automation**, like a `TreeView`. Drive it by opening
  the window that focuses it, or by tabbing.
- **Automation ids must differ between the main window's result view and a form's** (`MainResults`,
  `FormResults`; `ResultHeading` and `FormResultHeading`) — an owned window is a UIA descendant of
  its owner, which Phase 3 learned about buttons and is just as true of panes and of headings. It
  cost two hung test runs here in its third incarnation: with a form window open, a lookup from the
  main window found the form's heading, every wait for the main one timed out at thirty seconds, and
  the suite looked hung rather than wrong. **Prefix every named element in a non-main window.**
- **A bare `ContentControl` creates no automation peer at all**, so a view built on one is absent
  from the tree and cannot be scoped to in a test. `ResultView` reports itself as an unnamed group.

**Elsewhere:** §5 gains the result half of the schema engine; §6.1's hedge 5 is now discharged;
§10's Phase 4 entry is done; §11's v2 entry keeps `IResultSummarizer` for alt text.

---

## 8. What is outstanding

- **JAWS and Narrator have still not seen any of this**, carried forward from Phase 0. Every finding
  in §4 was heard in NVDA; three of them (§4.2, §4.4, §4.5) are about UI Automation properties that
  all three readers consume, and would be defects in any of them.
- **Item 25 of the Phase 0 checklist** — the application with no screen reader running — still wants
  a sighted pass, and Phase 4 adds a picture to look at for the first time.
- **A grid of 200 rows is built eagerly**, which is the cost of §7.6. It has not been measured on a
  slow machine.
