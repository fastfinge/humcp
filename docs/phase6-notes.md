# Phase 6 — The shell

**Goal:** settings, server add/remove/reconnect, configuration persistence,
`notifications/tools/list_changed` via `subscriptions/listen`, an announcement policy with rate
limiting — and the menus' contents (ROADMAP §10).

**Exit:** the app is configurable and usable without editing files by hand.

**Status: built, suite green, first manual pass walked.** Schema 869, Core 175, UI 77, the last
re-run after every fix the pass made. That re-run found one more on its own: the result pane's
placeholder ("No tool has been run yet") and its "no content" message had neither help text nor a
label, which only failed when the suite happened to reach them in that state — both now have help,
and a tab-order assertion that depended on which tool another test had last run no longer does. The first manual pass was driven through NVDA by the
screen reader bridge rather than by a person, under the bridge's *user* stance — ordinary keys and
reading commands only — and **it found seven defects that the suite had not** (§4), three of them
older than this phase. A pass by a person is still owed (§8).

Environment: .NET SDK 10.0.402, Windows 11 (10.0.26340), `ModelContextProtocol.Core` 2.2.0,
`JsonSchema.Net` 9.4.0, `FlaUI.UIA3` 5.0.0, xunit.v3 3.2.2, NVDA alpha-58137. No new package references.

---

## 1. What exists

```
src/Humcp.Core/
├─ ServerConfiguration.cs   servers.json in the mcpServers shape; Parse for pasted blocks
├─ ServerDescriptor.cs      + Environment, Url, Enabled; Split/JoinArguments moved here
├─ ServerRegistry.cs        every connection, kept in step with the file; ServerAnnouncements
├─ ServerConnection.cs      Changed event, Reconfigure/Stop, env vars, subscriptions/listen,
│                           coalesced re-listing, progress
├─ ServerForm.cs            the add/edit server schema, filled and read back from the boxes
├─ AppSettings.cs           settings.json, the Settings schema, the pane-width schema
├─ PaneLayout.cs            window and pane sizes from the screen and the content
└─ ToolProgress.cs          ToolProgress and ProgressThrottle (§4, pitfall 9)

src/Humcp.Schema/
├─ ResultSort.cs            how a table sorts, and what it says it did
└─ FormState.cs             LeadProblem: the problem announced is the one the caret lands on

src/Humcp.App/
├─ OwnFormWindow.cs         a form whose schema is humcp's own
├─ KeyboardHelp.cs          Help, Keyboard shortcuts
├─ MainWindow.xaml(.cs)     the registry, a node per server, menus, context menu, fitting,
│                           background speech, the deferred announcement
├─ ResultGrid.cs            sorting, the Applications-key menu
├─ ToolWindow.xaml.cs       progress, Withdrawn(), settings for history and optional fields
└─ WindowManager.cs         no window bigger than the screen

tests/Shared/TestServerLocator.cs   the fixture, found by both suites; nothing in src/ knows it
tests/Humcp.TestServer/ListChangeTools.cs   tools_toggle, tools_appeared, demo_progress
```

Deleted: `BuiltInServers`, the `HUMCP_STDIO_*` override, `Humcp.App`'s build-order reference to the
fixture.

## 2. What the shell looks like

- **File:** Add server (Ctrl+N), Import servers, Server settings (Alt+Enter), Reconnect (F5), Remove
  (Delete, twice), Settings (Ctrl+Comma), Exit.
- **Edit:** Copy server configuration, Copy tool schema, Clear parameter history.
- **View:** Servers pane width.
- **Help:** Keyboard shortcuts, Speech diagnostics, Connection diagnostics, About.
- **Applications key / Shift+F10:** on a tool, a server, an empty tree, and a result grid.

Every form in that list — add, edit, import, settings, pane width — is a JSON Schema in `Humcp.Core`
rendered by `FormBuilder` (§3.2).

## 3. Findings that change later phases

### 3.1 On 2026-07-28 a list change arrives only on a listen stream, and the SDK has no call for it

SEP-2575 removed the unsolicited channel. A server sends `notifications/tools/list_changed` only down
the response stream of a `subscriptions/listen` request, held open for the connection's life, and
`ModelContextProtocol.Core` 2.2.0 has a server-side handler for it and **no client call at all**.
`ServerConnection.Listen` sends it as a plain `JsonRpcRequest` whose answer never comes; the SDK puts
the per-request `_meta` on it unasked. **Measured both ways:** with `Listen` disabled the fixture's
change never arrived; with it, the tree changed in under a second. Older revisions still notify
unprompted, and the same notification handler hears both. Only sent when the server both speaks the
revision and advertises `tools.listChanged`.

### 3.2 humcp's own forms are generated, and two rules invert for them

The add-server, settings, import and pane-width windows are schemas, not XAML — so the whole of §7
applies to them free, and the manual pass found the forms themselves sound (§4.8). Two tool-form rules
invert, both because the schema is ours: **nothing is hidden** (every field is saved, §7.31's reason)
and **a problem blocks the save** (§5's "never block" defers to a server; here there is none).

And the omission rule cannot be used to read them back. "Cleared" and "untouched" both mean "not
sent", which is right for a server with its own default and wrong for a configuration, where the box
*is* the value. So the schema states no defaults, `Fill` puts values in the boxes after the form is
built, and `Read` reads the field states directly. A test pins the difference
(`A_cleared_box_means_empty_rather_than_unchanged`).

### 3.3 servers.json is the user's file

A save changes only the members humcp manages and leaves every other member of an entry, and of the
file, where it was; a file that is not JSON is never saved over. `Parse` accepts what other clients'
documentation says to copy: a whole `claude_desktop_config.json`, VS Code's `mcp.json` (comments,
trailing commas, `servers`), the bare block, one named entry with or without braces, one bare entry.
An `http` entry is kept and shown as unsupported until Phase 7.

### 3.4 A focused GridSplitter never receives an arrow key

The first route to resizing a pane handed the splitter the caret. With it focused, **an arrow press
never reached WPF's key routing at all** — not the splitter's handlers, not the window's, not with
`handledEventsToo` — while Escape on the same splitter did. Probed four ways, recorded, and not chased
further. §10's other permitted route was taken: View, Servers pane width is a generated form, and the
splitter is `Focusable="False"`, the mouse's route only.

### 3.5 Panes size themselves, because the user cannot see them

Raised by the developer after the suite was green: a screen reader reads a pane whole whatever its
width, so the person at the keyboard is the one person who cannot judge the layout, and a sighted
colleague sees whatever the defaults chose. `PaneLayout`: the main window takes 85% of the work area;
every other window is shrunk to fit the screen (a tool form asks for 800 high, and a laptop's work
area is often less); the servers pane is fitted to its widest line, at most half the window, never
squeezing the result pane below its minimum, and re-fitted as servers connect and the window
resizes. A width chosen on purpose — a drag, or the form — is kept and stops fitting until the form's
check box asks for it back.

## 4. What the manual pass found

Driven through NVDA by the bridge, silent capture, *user* stance. Seven defects, every one fixed and
re-heard. **None of them was visible to the suite**, which asserts where focus is and what a control
is named, never what was said.

### 4.1 A server finishing its start-up left NVDA on a node that no longer existed

The server's node is replaced when its state changes. WPF's keyboard focus moved to the new node and
NVDA was never told: report-focus answered with *silence*, and NVDA still held the old "connecting"
object. Every start-up and every F5. Fixed by re-focusing once the new item is loaded and raising the
focus event outright (`Refocus`). Afterwards: *"humcp test server, connected, 30 tools … expanded"*.

### 4.2 The refusal named one field and the caret went to another

*"2 problems to fix before this can be saved. Command: This one is required"* — and the caret in Name.
The announcement took the report's first issue, sorted alphabetically; the caret goes to the first in
tab order (§7.22). **The tool form and the question window had the same defect since Phase 3 and 5.**
`FormState.LeadProblem` picks the issue the caret will land on; all three windows use it.

### 4.3 Removing a server was six seconds of silence

The second Delete waited for the child process to shut down before the tree changed or anything was
said — long enough to press Delete a third time. Removal now updates the file, the list and the tree
at once and stops the process in the background.

### 4.4 A tool that changed the list lost its own answer

The tool finishes and the list-change notification arrives within milliseconds; the change replaced
the completion in the status line and NVDA spoke only the change. phase5-notes 4.4 in a new place.
Merging the two into one line was tried and said the answer twice. **What works is waiting:** an
unprompted announcement is held until the status line has been quiet for two seconds. Heard after:
*"Tool call complete …"*, then *"humcp test server changed its tools: 1 added, A tool that appeared.
31 tools now."*

### 4.5 Shift+F10 in a grid opened the window's system menu

*"System, Alt+Space … Restore"* where sorting was promised. WPF opened nothing for an element with no
context menu at the moment of the key press. The grid and the tree now have an empty menu from the
start, filled when it opens.

### 4.6 After a sort, Up and Down moved the caret and said nothing

Left and Right still spoke; vertical moves were silent in every column, and the focused cell reported
role "unknown". An in-place sort of the collection view left the grid's automation peers describing
rows that had moved. Sorting now rebinds the rows in the new order and focuses the top cell of the
sorted column. Afterwards each row is read, and entering the column says *"Score, sorted descending,
column 4"*.

### 4.7 Help text lost the sentence that mattered

Help is the first sentence of a description (§4, pitfall 1), and humcp's own descriptions put the
important part second: that environment variables are kept in plain text, that an argument with a
space needs quotes, that an empty working directory means humcp's own, that a password-like value is
never remembered. Rewritten so each is in the first sentence. **humcp broke its own rule about other
people's descriptions in its own.** Also: the width box said "required" while the Fit box made it
irrelevant; it is optional now.

### 4.8 What the pass confirmed

The generated forms read correctly field by field — name, "required", help, access key — and the
buttons announce their shortcuts in words. Tab order wraps cleanly. Add server says *"Added Second
fixture. Connecting."*, lands on the new node and announces the connection. Delete asks first. The
error of a server that will not start is heard on its node. Type-ahead is about 50 ms a key.

### 4.9 Heard and left

- **"unavailable"** as a replaced tree node goes, before the new one is announced. Harmless, and the
  fix would be reusing containers rather than replacing nodes.
- **"menu, menu"** on opening a context menu, WPF's popup and menu both announcing.
- **A menu item's gesture text is not spoken**, only its access key — true of the main menu since
  Phase 1.
- **The Fit check box was announced twice** on opening the pane-width form, which no other own form
  does. Not reproduced deliberately.
- **The Applications key itself** was not exercised: the bridge's name for it was not found, and
  Shift+F10, the same command, was used.

## 5. Decisions taken

| Decision | Why |
|---|---|
| No built-in server; the suite writes a config | §10's carried item. The fixture is a test asset, and an app that knows where its tests live is shipping them. |
| humcp's forms are schemas | One generator, one contract, one set of defects already fixed (§3.2). |
| Remove is asked twice, not confirmed in a dialog | An application with no modal windows; the same warn-once a submit already uses (§5). |
| prism speaks when humcp is in the background, on by default | §13 left it a judgement, and the developer made it at the exit: a call finishing unheard is worse than an interruption. Settings turns it off. |
| Unprompted announcements wait for quiet | §4.4. The general announcement policy, of which Phase 5's 1.2-second "still running" was the first instance. |
| Progress: none in the first 2 s, then at most one per 2 s, none after the answer | Pitfall 9, with phase5-notes 4.4 folded in. |
| A pane width is a setting, not a focusable splitter | §3.4. |
| Panes and windows fit the screen and the content | §3.5. |

## 6. What Phase 6 deliberately does not do

- **Streamable HTTP.** An `http` entry is read, kept and shown as unsupported; Phase 7.
- **Detect a server process that dies mid-session.** Phase 7's "robust disconnect handling".
- **Confirm a `destructiveHint` tool before running it.** §3's table promises it and no phase built
  it; found while writing this phase. Carried to Phase 7.
- **Remember the main window's size.** It is fitted to the screen each start.

## 7. Additions to the roadmap — proposed

For §7:

33. **Focus moved is not focus announced.** Replacing an element that has focus and focusing its
    replacement in the same pass moves WPF's focus and tells the screen reader nothing (§4.1); a grid
    re-sorted in place goes silent on vertical moves (§4.6). Defer the focus until the new element is
    loaded, raise the event, and prefer rebuilding to mutating a view a reader is in.
34. **Something unprompted waits for quiet.** A live region announces its newest text, so a message
    nobody asked for, arriving just after one somebody did, erases it (§4.4).
35. **The announcement and the caret agree.** Name the problem the focus lands on (§4.2).
36. **Our own help text obeys pitfall 1.** The first sentence is all that is spoken (§4.7).

For the keyboard map: Ctrl+N, Alt+Enter, Delete twice, Ctrl+Comma, Shift+F10 on grids.

## 8. What is outstanding

- **Background speech by ear** — on by default since the exit (§5), and not exercised by the bridge
  pass, which needs another program in front to hear it.
- **NVDA is the manual matrix from here on.** The developer's judgement at the Phase 6 exit: JAWS and
  Narrator agree with NVDA nearly always, so the carried "JAWS and Narrator" item is closed rather
  than carried a fifth phase.
- Nothing is committed yet.
