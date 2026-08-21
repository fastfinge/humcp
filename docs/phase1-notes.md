# Phase 1 — the walking skeleton

**Goal:** solution structure, one hard-coded stdio server, `tools/list` into a real tree, a
zero-argument tool call, and the text result in a read-only `TextBox` (ROADMAP §10).

**Status: complete.** 81 automated tests green, and the manual NVDA pass green across all nineteen
checks (§6) — which is what actually closed the phase, since the exit criterion is "usable with a
screen reader" and no assertion can say that. The pass found one defect the automated suite could
never have found: every announcement was being spoken twice (2.7).

Outstanding and non-blocking, carried forward from Phase 0: the JAWS and Narrator passes.

Environment: .NET SDK 10.0.400, Windows 11 (10.0.26340), `ModelContextProtocol.Core` 2.2.0,
`Prismatoid` 0.4.0, `FlaUI.UIA3` 5.0.0, xunit.v3 3.2.2.

---

## 1. What exists

```
Humcp.slnx
├─ src/Humcp.Schema/     net10.0          text hygiene, OperationSafety. No MCP, no WPF.
├─ src/Humcp.Core/       net10.0          registry, connection, catalogue, neutral results.
├─ src/Humcp.Speech/     net10.0-windows  IAnnouncer over prismatoid, plus the no-op.
├─ src/Humcp.App/        net10.0-windows  WPF: tree, result box, status line.
└─ tests/
   ├─ Humcp.TestServer/    a real stdio MCP server, 10 tools, deliberately hostile
   ├─ Humcp.Schema.Tests/  34 tests
   ├─ Humcp.Core.Tests/    33 tests, driving the fixture over a real pipe
   └─ Humcp.App.UiTests/   14 tests, walking the live UIA tree with FlaUI
```

The dependency rule holds and is now enforced by what each project references: **`Humcp.App` has
no reference to the MCP SDK at all.** Everything crossing out of `Humcp.Core` is a neutral model —
`ToolInfo`, `CatalogueNode`, `ToolCallOutcome`, `OperationSafety` — so the §6.1 hedges are
structural rather than aspirational.

### The round trip

Startup connects on a background task, so the window is alive and navigable while the child
process starts. `tools/list` becomes `ToolInfo` records; the tree is rebuilt from an immutable
`ServerSnapshot`. Arrowing to a tool and pressing <kbd>Enter</kbd> calls it, renders the text into
the read-only result box, and announces the completion through a polite live region with a route
to the output ("Press F6 to review the result"). Nothing blocks the UI thread; the fixture's
two-second tool is there to prove it.

---

## 2. Findings that change later phases

### 2.1 Zero arguments means `"arguments": {}`, not an absent member

The first version called `CallToolAsync(name)` with no argument dictionary, which the SDK sends as
a request with no `arguments` member. Our own fixture accepted it. The real
`@modelcontextprotocol/server-everything` did not:

```
MCP error -32602: Input validation error: Invalid arguments for tool get-env:
Invalid input: expected object, received undefined
```

A zero-argument call still has to carry an empty object. `ServerConnection.CallAsync` now passes
one explicitly, and the comment there says why so nobody tidies it away. This is the single most
likely thing to have silently broken against half the servers in the world, and it took connecting
to one real server to find it.

### 2.2 A live region must not carry a static `AutomationProperties.Name`

The status bar started life as `AutomationProperties.Name="Status"`, copied from the Phase 0
spike. A `TextBlock`'s accessible name **is** its text; naming it "Status" means every live-region
announcement says the word "Status" and never the message. The name is now unset and the `x:Name`
(which becomes the automation id) is how tests find it.

Phase 0's checklist item 11 — "is prism actually needed here?" — cannot be answered honestly until
this is right, because the live region was announcing the wrong string.

### 2.3 A WPF `Window` reports its content as its name

<kbd>F1</kbd> opens a details window holding one read-only text box. Its accessible name came out
as *the entire body text* — ten thousand characters of tool description announced as the window
title. A `Window` is a `ContentControl`, and its automation peer falls back to the plain text of
its content when no name is set. `AutomationProperties.SetName` on the window fixes it, and a UI
test now holds it fixed.

Same family as the Phase 0 findings: a WPF default doing the wrong thing in one line of XAML.

### 2.4 Sanitising for speech and sanitising for review are different operations

`TextSanitizer.Clean` collapses runs of whitespace, which is right for a name or a help string
heading for a synthesiser. It is wrong for a result. Results arrive as JSON, as tables, as aligned
columns, and the read-only box exists so they can be read line by line (§7.9) — collapsing the
indentation throws away the only structure such a result has.

So there are two operations: `Clean` for anything spoken, `CleanMultiline` for anything reviewed.
The second keeps line structure and indentation, converts tabs to a single space, and still
removes every control and formatting character. The 200-line fixture tool caught this: its aligned
line numbers came back collapsed.

### 2.5 The SDK really does negotiate backwards

Against our fixture (built on SDK 2.2.0) the negotiated version is **2026-07-28**. Against
`npx -y @modelcontextprotocol/server-everything` it is **2025-11-25**, and everything else worked
unchanged: 13 tools, titles, annotations, parameter counts, first-sentence help text, and a
zero-argument call returning content. Pitfall 8 is retired for v1's purposes — with the caveat that
the negotiated version is now on the server's tree node, so the next surprise is diagnosable
without a debugger.

That run is also the first evidence for the thesis: a third-party server nobody wrote any code for
turned into a navigable accessible tree.

### 2.6 Three facts about writing the UI tests

- **A `TreeView` is not focusable once it has items.** `SetFocus` on the tree itself throws;
  focus belongs to the selected `TreeViewItem`. Tests must focus an item.
- **Disabled buttons leave the tab order**, so a tab-order test has to set up a state where every
  control it expects to reach is enabled. Ours starts by focusing a runnable tool.
- **Taking the foreground has to be retried, not attempted.** Windows declines
  `SetForegroundWindow` outright when the calling process is not already in front — which is the
  normal case while the other two test projects are running alongside this one. A single attempt
  made all seven keyboard-driven tests fail together, in three seconds, on a busy machine, and pass
  on their own. The guard still refuses to type when it cannot get the foreground (§9); it now
  spends ten seconds trying first.

All three are the sort of thing that produces a flaky test rather than a failing one, so they are
written down.

### 2.7 The live region is enough, and prism on top of it speaks everything twice

**Found by ear, in the first manual pass** — which is exactly the class of defect no UIA assertion
can catch. Running a tool was announced twice: once by NVDA reading the polite live region, once by
prism speaking the same string.

Phase 0 left this open (checklist item 11: "is prism actually needed?") and wired both paths so the
question could be settled by listening. It is now settled: **a WPF live region reaches NVDA on its
own.** A completion, an error count, a connection state — anything that has a place on screen —
belongs in the status line and nowhere else.

That narrows prism's job considerably, and sharpens §7's rule rather than contradicting it. "UIA
does the nouns, prism does the verbs" was the right instinct with the wrong boundary: a live region
*is* UI Automation, and it carries verbs perfectly well. The boundary that survives contact with a
screen reader is:

> prism is for text with no on-screen representation at all. If it can be put somewhere the user
> could read it, put it there and let the screen reader announce it.

Two announcements were removed: the status line no longer speaks, and <kbd>F6</kbd> no longer
speaks the pane name — focus lands on a named control whose name *is* the pane name, so the screen
reader was already saying it.

Which leaves prism with nothing to say in Phase 1. The seam stays exactly where it is (§4, pitfall
7 wants it, and Help → Speech diagnostics still proves it loads and picks a backend), unused until
something genuinely off-screen turns up. The known candidate is an event arriving while humcp is
*not* the foreground window — a slow call finishing, or a connection dropping, while the user is in
another application, where no live region will be read to them. That is a policy question for
Phase 6's announcement policy, not something to invent now.

---

## 3. Decisions taken

Each is cheap to reverse now and annoying to reverse later, so they are stated rather than left
implicit.

| Decision | Why |
|---|---|
| `Humcp.slnx`, not `Humcp.sln` | The Phase 0 spike already used the new format and the .NET 10 toolchain reads it natively. Now recorded in ROADMAP §6.0, along with the rest of this table. |
| `Humcp.Schema` and `Humcp.Core` target `net10.0`, not `net10.0-windows` | Neither needs Windows, and the TFM is what makes "pure" checkable rather than merely intended. `Humcp.Speech` and `Humcp.App` stay `net10.0-windows`. |
| `Humcp.Speech` references `Humcp.Schema` | For `TextSanitizer` only. Text heading for a synthesiser needs the same hygiene as text heading for UIA, and one implementation beats two. The rule that matters — nothing points back at `Core` or `App`, and `Schema` stays pure — is intact. |
| The fixture server lives in `tests/`, and `Humcp.App` has a build-order-only reference to it | So `dotnet run --project src/Humcp.App` works from a clean clone. It is deleted in Phase 6 when servers come from configuration. |
| `HUMCP_STDIO_COMMAND` / `_ARGS` / `_NAME` override the hard-coded server | Lets a real server be tried by ear today without a code edit. It is how finding 2.1 was found. |
| xunit.v3, central package management | Current, and the pins live in one file. |
| The FlaUI suite runs as part of `dotnet test` | Chosen deliberately: it takes the foreground and sends keystrokes, and it refuses to type unless the app owns the foreground. |

---

## 4. Additions to the accessibility contract (§7) — accepted

All five were accepted after the manual pass and are now in `ROADMAP.md`: the first four as
**§7.16–19**, the fifth as a rewrite of §7's core rule.

1. **A live region must not have a static accessible name** (finding 2.2). Its name is its
   message; naming the element makes it announce the label forever.
2. **A window that holds a single content control needs an explicit
   `AutomationProperties.Name`** (finding 2.3), or WPF reports its whole content as the window's
   name.
3. **Declining an action must say why, and how far away the ability is.** Pressing Enter on a tool
   that needs parameters answers "Echo needs 1 parameter. Generated forms arrive in Phase 3." A
   generated UI will always have edges it cannot yet handle; silence at an edge is
   indistinguishable from a bug.
4. **Text for review and text for speech need separate sanitising** (finding 2.4). Worth stating
   beside §7.9, since the rule about the result box being a `TextBox` is only useful if what goes
   into it still has its shape.
5. **Restate §7's core rule** (finding 2.7). "UIA does the nouns, prism does the verbs" read as
   though completions and error counts were prism's, and they are not — a polite live region
   carries them, and prism on top of it speaks everything twice. The rule now reads: *prism is only
   for text with no on-screen representation at all; anything that can be shown somewhere goes
   there, and the screen reader announces it.* The examples in §7's opening paragraph — "Tool call
   complete, 3 results", "5 validation errors" — moved to the live-region side of the line.

---

## 5. What Phase 1 deliberately does not do

Named so nobody mistakes an absence for an oversight: form generation (Phase 3), any tool with
required arguments, MRTR and elicitation (Phase 5), prompts and resources, more than one server,
`structuredContent` rendering (Phase 4), a filter box, a window manager, cancellation of an
in-flight call (Phase 7), and the Streamable HTTP transport (Phase 7).

Two smaller ones, both inherited: there is still no keyboard route to resizing the splitter
(phase0-findings 2.4, now written into the Phase 6 entry in §10), and the result pane is one pane
rather than the non-modal window-per-result model of §8.

---

## 6. The manual pass — run under NVDA 2026.1, all green

The full Phase 0 checklist (phase0-findings §6) is the script for a phase with a generated form in
it; this is the subset Phase 1 has surface for, and it is the script to re-run at the Phase 2 and
Phase 3 exits. Item 5 is the one that earned its place: it failed, and nothing mechanical could
have told us.

| # | Check | Expected | Result |
|---|---|---|---|
| 1 | Arrow through the tree | Four levels; server status, tool count and protocol heard on the server node |  Pass |
| 2 | Arrow onto a tool | Name, then "read-only" or "destructive", then the parameter count if any |  Pass |
| 3 | Is the help text heard? | First sentence of the description, not the ten thousand characters behind it |  Pass |
| 4 | <kbd>Enter</kbd> on "Server information" | Completion announced once, with the line count and the F6 route |  Pass |
| 5 | Anything heard twice? | Nothing double-spoken — §7's core rule holds | **Failed, fixed, re-run: Pass.** Every tool run was announced twice (finding 2.7) |
| 6 | The status line | Is the completion heard from the live region *without* prism? (Phase 0 item 11, now askable) | **Yes** — NVDA reads the polite live region unaided. prism is no longer used for it |
| 7 | <kbd>Enter</kbd> on "Echo" | "Echo needs 1 parameter. Generated forms arrive in Phase 3." |  Pass |
| 8 | <kbd>Enter</kbd> on "Slow tool" | Window stays responsive for the two seconds; tree still arrows |  Pass |
| 9 | <kbd>Enter</kbd> on "Always fails" | Error announced as an error, with the reason |  Pass |
| 10 | <kbd>Enter</kbd> on "Tiny image" | "[Image, image/png, 68 bytes]" is a sensible thing to land on |  Pass |
| 11 | <kbd>F6</kbd> / <kbd>Shift+F6</kbd> | Pane announced once, by the screen reader alone — no second voice (finding 2.7) |  Pass |
| 12 | Result box, arrow keys | Line-by-line review works; a 200-line result is navigable; text selectable |  Pass |
| 13 | <kbd>F1</kbd> on "Hostile text" | Details window announced by title, not by its contents (finding 2.3) |  Pass |
| 14 | <kbd>Esc</kbd> in the details window | Closes, focus returns somewhere sane |  Pass |
| 15 | <kbd>Alt</kbd>, then arrows | Menu bar reached and arrowed; never tabbed to |  Pass |
| 16 | <kbd>Tab</kbd> around the main window | Tree item, Run tool, Reconnect, Result — and nothing else |  Pass |
| 17 | Help → Speech diagnostics | Which backend prism chose, readable |  Pass |
| 18 | <kbd>F5</kbd> | Reconnect announced; tree rebuilt; selection sane afterwards |  Pass |
| 19 | With NVDA closed | App still fully usable on the no-op announcer |  Pass |

Then the same list under JAWS, and a quick pass under Narrator — both still outstanding from
Phase 0, and both cheaper to do now that there is a real app to do them against.

A second manual run is worth doing with the environment override pointed at a real server:

```
set HUMCP_STDIO_COMMAND=npx.cmd
set HUMCP_STDIO_ARGS=-y @modelcontextprotocol/server-everything
```

Thirteen tools written by strangers, in an accessible tree, with no code that knows anything about
them. That is the thesis, and it is worth hearing rather than reading.
