# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status: v1.0.0 released; 1.x is fixes, v2 is next (§11)

**`docs/ROADMAP.md`** is the specification. Read it before doing anything. Section references
below (§) point into it.

**Phase 0 is complete and WPF is confirmed** — the spike's four-level tree kept its full hierarchy
in the UIA tree, and prism detects and speaks through a running NVDA. WinForms is no longer a
fallback. The six defects it found are recorded in **`docs/phase0-findings.md`** and have been
folded into §7.12–15 and §9; that document also holds the manual screen-reader checklist, which is
the script to re-run at each later phase exit.

**Phase 1 is complete**: the walking skeleton connects to a hard-coded stdio server, lists its
tools into a four-level tree, runs a zero-argument tool from the keyboard, and renders the text
result into a read-only `TextBox`. 81 tests including a FlaUI suite over the live UIA tree, and a
manual NVDA pass green across nineteen checks — which is what closed it, and which found a defect
no assertion could (everything was spoken twice). Record in **`docs/phase1-notes.md`**; its four
new contract rules are now §7.16–19 and its fifth rewrote §7's core rule. Layout decisions taken
there — solution format, target frameworks, project references — are §6.0.

**Phase 2 is complete**: `Humcp.Schema` in full — three tiers, `$ref` with per-branch cycle
detection, validation over `JsonSchema.Net` with every message written here, and the corpus.
**95.0% of the corpus's 785 real top-level parameters render at Tier 1 or 2, 91.2% at Tier 1**, and
every one of the 213 schemas produces a form. 813 tests. Record in **`docs/phase2-notes.md`**;
its findings are now §5 (two new rules), §6.2, §9's corpus provenance, §11's Phase 7 entry, and
three answers in §13.

**Phase 3 is complete**: generated forms, eleven templates covering the whole control vocabulary,
the optional toggle, MRU history, validation surfacing, a sub-form window for a row of an object
list, and the §8 window model. 902 tests, 36 of them over the live UIA tree. **The manual pass found
nine defects and the automated suite had found none of them.** Record in
**`docs/phase3-notes.md`**; its findings are now §4 pitfall 4, §5's "what a filled-in form means",
§6.3, §7.20–26, §8's window model and §9.

**Phase 4 is complete**: result rendering. Every kind of content has a control of its own — text in
a caret-navigable box, a picture with Save and Open, a link with an action only where humcp will
follow one, an embedded resource as text or as a file, a table as a real `DataGrid` with column
headers, anything else structured as a tree read "label, value". 1,013 tests, 51 over the live UIA
tree.

**The corpus decided the component's shape: 25 of 213 tools publish an `outputSchema` at all**, and
17 of those 25 wrap the answer in one property (13 of them a bare string the tool already sent as
text). So shape comes from the value, names come from the schema when there is one, the wrapper
comes off, and structured content that merely repeats the text is not drawn twice.

**The manual pass found eight defects, and again the suite had found none** — including two that had
been true since Phase 3 — and re-checking the fixes by ear found a ninth. Record in
**`docs/phase4-notes.md`**; its findings are now §5's "the other direction", §6.4, §7.27–30, §9 and
the keyboard map.

**Phase 5 is complete**: MRTR and elicitation. A server can stop in the middle of a tool call and
ask, and the question becomes a generated form in a window of its own — the same `FormBuilder`, the
same templates, the same `FormState`. 1,046 tests, 70 over the live UIA tree.

**Most of the protocol work turned out to be the SDK's.** `CallToolAsync` resolves an
`input_required` result through the registered handlers and retries the original request itself,
inside one `await`; humcp never sees the result type and never mints the new id. What it supplies is
a handler that opens a window — and an `AsyncLocal` set in `CallAsync` to route the question back to
the window whose call provoked it, because the handler is per connection and the question belongs to
one call.

**§3's claim that Tier 1 covers the whole elicitation subset is now measured, not assumed**: all
eight of the SDK's schema shapes render at Tier 1, including the two titled enumerations that reach
the client as `oneOf` and `anyOf` of constants.

**The manual pass found ten defects and the suite had found none** — the third phase running, and
the count is rising. Five were in code older than Phase 5, two of them on paths no earlier phase
could reach; re-checking the fixes by ear found the tenth. Record in **`docs/phase5-notes.md`**; its
findings are now §3's two elicitation modes, §5's elicitation exception, §6.5, §7.31–32, §9 and the
keyboard map.

**Phase 6 is complete**: the shell. No built-in server —
`servers.json` in the `mcpServers` shape, edited through Add server (Ctrl+N), Import, Server
settings (Alt+Enter), Reconnect (F5) and Remove (Delete twice); settings, menus, `tools/list_changed`
through a raw `subscriptions/listen` (the SDK has no client call, and on 2026-07-28 nothing arrives
without it), rate-limited progress, grid sorting, panes fitted to the screen and the content.
**humcp's own forms are JSON Schemas rendered by `FormBuilder`** into `OwnFormWindow`, filled and
read back from the field states because the omission rule cannot tell "cleared" from "untouched".
The first manual pass was driven through NVDA by the screen reader bridge and found seven defects
the suite had not. Record in **`docs/phase6-notes.md`**; proposed rules §7.33–36.

**Phase 7 is complete, and humcp 1.0.0 is released** (github.com/fastfinge/humcp): Streamable
HTTP (unauthenticated), a server that dies is reported, Escape stops a call, a destructive tool
confirms, a malformed schema costs one tool its form rather than the server its tools, every server
ends with humcp (a job object), and unexpected errors go to `%LOCALAPPDATA%\humcp\errors.log`.
Released as a per-user MSI (WiX 7, `installer/`) and portable zips by `.github/workflows/release.yml`
on a `v*` tag; CI (`ci.yml`) builds and runs Schema and Core. Record in **`docs/phase7-notes.md`**.

**1.1** reads JSON sent as text as the structure it is, edits a list entry in place with F2, and
returns focus to humcp when one of its windows closes. NVDA pass and its two defects in
**`docs/v1.1-notes.md`**.

**2.0 has started on `main`** (2026-10-20); `release/1.x` was cut from it carrying 1.1, which is
waiting on a SignPath certificate before it is tagged there. First piece of v2: the local-LLM
spike, §11.0–11.1.

**Versioning:** 1.x releases are bug fixes from user reports; 2.0 and 3.0 follow §11. A fix that
visibly changes what a user sees for the same call is a minor release (1.1), not a patch; one that
does not is a patch. Something unusable with a screen reader is a defect, not a missing feature,
however it came to be unbuilt — 1.1's JSON-in-text results are the precedent.

**Branches: one per major version, tags for everything else.** 1.x lives on `main` until 2.0 work
starts; then `release/1.x` is cut from `main`, 1.x fixes and their `v1.x.y` tags go there, and
`main` is 2.0. A fix needed in both is made on `main` and cherry-picked to `release/1.x`. No branch
per minor: only the newest 1.x is supported, and an old minor can still be branched from its tag
(`git switch -c release/1.0 v1.0.0`) if it is ever needed. When the first `release/*` branch is
cut, add it to `ci.yml`'s push branches. To release:
add a section to `CHANGELOG.md`, run the UI suite and an NVDA pass locally, then tag `vX.Y.Z` and
push the tag. The release workflow refuses a tag with no changelog section.

**Licence:** 0BSD for humcp's own code; `THIRD-PARTY-NOTICES.md` and `licenses/` for what it ships.
Code signing is not set up yet (SignPath Foundation, then MSIX through the Microsoft Store).

**Manual testing is NVDA only from Phase 6 on** — the developer's call: JAWS and Narrator agree with
NVDA nearly always. The NVDA pass can be driven through the screen reader MCP bridge (`user`
stance), which is how Phase 6's was done. Still outstanding: the picture Phase 4 puts on screen has
had no sighted pass.

## What humcp is

A Windows desktop app that generates accessible, screen-reader-first UI **automatically** from any
MCP server's published metadata. Tree of servers → tools → generated form → rendered result. No
per-server code. The thesis: MCP is accidentally an accessibility API, because it forces servers to
publish machine-readable, self-describing capability metadata — exactly what a UI generator needs.

Blind and low-vision users are the primary audience. Where accessibility and visual polish
conflict, accessibility wins.

## ⚠️ MCP spec version: 2026-07-28

**Your training data almost certainly predates this revision, and the differences are breaking.**
Do not write MCP code from memory — check the spec or the SDK.

Things that were true in earlier revisions and are now **wrong**:

| Don't write | Reality in 2026-07-28 |
|---|---|
| `initialize` / `notifications/initialized` handshake | Removed. Protocol is stateless; every request carries version + capabilities in `_meta` |
| `Mcp-Session-Id`, protocol-level sessions | Removed. Cross-call state uses server-minted handles passed as ordinary tool arguments |
| Server-initiated `elicitation/create` / `sampling/createMessage` / `roots/list` | Removed. Replaced by **MRTR**: `tools/call` returns `resultType: "input_required"` with `inputRequests` + opaque `requestState`; client retries with `inputResponses` and a **new** JSON-RPC id. **SDK 2.2.0 does all of that for you** — register handlers, `await CallToolAsync`, and a three-round-trip call returns once as `"complete"` |
| Elicitation is a form | Two modes: `form` and `url`. humcp declares `form` only (§3) |
| `ping`, `logging/setLevel`, `resources/subscribe` | Removed. Subscriptions go through `subscriptions/listen` |
| Roots, Sampling, Logging as things to build on | **Deprecated** |
| Results without `resultType` | All results carry `resultType`; treat a missing one from older servers as `"complete"` |
| `inputSchema` restricted to simple shapes | Loosened to any JSON Schema 2020-12 keyword, including `$ref`, `oneOf`, `if`/`then`/`else` |

Also note: `completion/complete` supports only `ref/prompt` and `ref/resource`. **There is no
`ref/tool`** — tool arguments cannot be autocompleted by the server, which is why local MRU history
exists (§4, pitfall 3).

Never hand-roll the protocol. `ModelContextProtocol.Core` 2.2.0 implements this revision and
negotiates back to older servers — confirmed in Phase 1: 2026-07-28 against our fixture,
2025-11-25 against `@modelcontextprotocol/server-everything`, same code either way. Phase 5 is the
strongest case for that rule so far: the entire MRTR retry loop is inside the SDK, and the parts of
it humcp would have written by hand are the parts it does not have to.

**Sampling and roots are declined by handlers that refuse, not by handlers that are missing.** With
none registered the SDK throws a message about handler registration; with one that throws
`UnsupportedInputRequestException` the user hears a sentence about their tool. Both APIs are
deprecated and referenced on purpose — a client that stops compiling against a deprecated request is
a client that stops being able to turn one down.

**A zero-argument call still sends `"arguments": {}`.** Omitting the member is a different wire
shape, and real servers reject it ("expected object, received undefined"). `ServerConnection`
passes an empty dictionary on purpose; do not tidy it away.

## Confirmed decisions — do not silently reverse

Each was made against a specific alternative for a specific reason. Reopening one is fine; doing it
without saying so is not.

- **WPF**, not WinUI 3 (§2.1). WinUI 3 controls are not native Win32, and
  [microsoft-ui-xaml#10483](https://github.com/microsoft/microsoft-ui-xaml/issues/10483) — open,
  unassigned — makes its `TreeView` announce every node at level 1 to NVDA. The tree is this app's
  primary navigation. Phase 0 confirmed WPF keeps all four levels; WinForms is no longer a fallback.
- **No LLM in v1.** Seams exist (`IResultSummarizer`, `IToolSuggester`) as null implementations;
  they get filled in v2.
- **v2's LLM is local, through ONNX Runtime GenAI on Windows ML** (§11.0), not SharpMind (no
  vision) and not DirectML (maintenance mode). No API keys, nothing leaves the machine. Three
  pinned model tiers — CPU only (the default), low-end GPU, high-end GPU — and the §11.1 spike
  decides whether each one earns its place. Windows 10 is not supported from v2.
- **v1 transports: stdio + *unauthenticated* Streamable HTTP.** OAuth is v3 (§11).
- **Do not build `ICapabilityProvider` before v4** (§12). Two implementations before an
  abstraction; extracting an interface from one implementation is guessing at the joints.
- **`JsonSchema.Net` for assertion semantics, our own words for every message.** The one package
  `Humcp.Schema` references. A hand-rolled subset would disagree with the server about what is
  valid; a library's error strings are not sentences to say to someone who cannot see the red box.
- **`oneOf` stays at Tier 3, and the `discriminator` promotion is not worth building yet** —
  measured against the corpus, not assumed: zero of the 18 real union parameters are
  constant-discriminated (phase2-notes 3.3). Re-measure at v4 by re-running the harvester.
- Target `net10.0-windows`. Pinned: `ModelContextProtocol.Core` 2.2.0, `prismatoid` 0.4.0
  (very new — wrap it, and keep the no-op fallback working), `JsonSchema.Net` 9.4.0.

## Architecture

```
src/Humcp.Schema/   net10.0          JSON Schema -> FieldModel -> FieldState on the way in,
                                     (schema?, value) -> ResultNode on the way back. Pure.
src/Humcp.Core/     net10.0          Server registry, connections, tool catalogue, MRTR, MRU,
                                     and the two shell rules (MediaTypes, LinkPolicy). No UI.
src/Humcp.Speech/   net10.0-windows  IAnnouncer over prismatoid, plus a no-op implementation.
src/Humcp.App/      net10.0-windows  WPF: views, templates, view-models, window manager,
                                     result view, the window a server's own question opens, and
                                     the one place that calls the shell.
tools/Humcp.Harvester/               Starts real MCP servers, writes their schemas into the corpus.
tests/              TestServer (a real stdio fixture server), Schema.Tests (golden),
                    Core.Tests (the round trip over a real pipe), App.UiTests (FlaUI),
                    fixtures/schemas (the corpus), fixtures/golden (transcripts + coverage.txt)
```

The two pure libraries target `net10.0` deliberately — neither needs Windows, and the TFM is what
makes "pure" checkable rather than merely intended. `Humcp.Speech` references `Humcp.Schema` for
`TextSanitizer` alone: text heading for a synthesiser needs the same hygiene as text heading for
UIA, and one implementation beats two. **`Humcp.App` does not reference the MCP SDK at all** —
everything crossing out of `Humcp.Core` is a neutral model (`ToolInfo`, `CatalogueNode`,
`ToolCallOutcome`, `OperationSafety`).

**Dependency rule: `App` → `Core` → `Schema`, never backwards.** `Humcp.Schema` takes a
`JsonElement` and returns a `FieldModel`; it must not reference the MCP SDK or WPF. This one rule
is what makes v4's OpenAPI support cheap, since OpenAPI 3.1's Schema Object *is* JSON Schema
2020-12. Four supporting hedges are in §6.1 — keep MCP vocabulary out of `FieldModel`, keep its
opaque `Binding` slot, model safety as a neutral `OperationSafety` record, and have the result
renderer take `(schema, value)` rather than a `CallToolResult`.

`Humcp.Schema` is the core IP and is pure logic — build and test it without a UI. **That now
includes the editable form state** (`FieldState`, `FormState`): §6.3 explains why view-models by
shape live below the App, and the short version is that what an empty box means is a rule and not a
pixel. WPF binds to them directly; the App owns templates, windows and keystrokes.

## The window model

Non-modal and multi-document (§8), built in Phase 3 after the first manual pass reported the form
windows "read like dialogues rather than windows". Every window humcp opens — forms, the sub-form
for a row of an object list, F1 details, diagnostics — carries its own menu bar with a `Window` menu
listing every open window, numbered and with the current one checked. Ctrl+1…9 switches; Escape and
Ctrl+W close. **Only the main window is in the task bar and in Alt+Tab**: a session across several
servers can open a great many windows, and burying every other application on the machine under them
is not a trade worth making when the same list can be named and numbered inside humcp.

`WindowManager` owns all of this. Anything that opens a window passes it through and calls `Track`
before `Show`.

## What a filled-in form sends

**A value the user did not choose is not sent.** A box still holding what the schema's `default`
put there says nothing; a required field is the exception, because the call has to state it. One
expression covers every kind of field — `IsRequired || value != initial` — including the case that
looks like an exception: a check box that started ticked because the schema said so and has been
unticked sends `false` out loud, since otherwise a stated default could not be turned off.

Found by a corpus test on five real servers at once, not by reasoning (phase3-notes 3.2). Do not
"fix" it by sending prefilled defaults.

**There is exactly one refusal, and it is not a validation failure.** Unparseable text in a Tier 3
editor means there is no argument object to construct, which is a different thing from suspecting
the server will decline. Everything else warns once, says how many problems and how to overrule,
and sends unchanged on the second press.

## What a rendered result shows

`ResultBuilder.Build(schema?, value)` — §6.1's fifth hedge, and **the schema is optional because
almost nothing publishes one**: 25 of the corpus's 213 tools have an `outputSchema` at all. Shape is
read from the value; a schema, where there is one, supplies names and descriptions, so a server's
own `title` reaches the user through a client that has never heard of it.

Three rules came from the corpus rather than from taste, and all three are about SEP-2106 forcing an
object where the tool has a string or a list:

- **A wrapper of one property comes off** and its name is kept — `{"entities": […]}` is shown as
  *"Entities, table, 8 rows, 5 columns"*, not as *"Structured result, 1 field"*. 17 of the 25.
- **Structured content that repeats the text is not drawn twice.** 13 of the 25 are
  `{"content": "…"}` holding the string already sent as text; drawing both would say everything
  twice, which is phase1-notes 2.7 in a new place. The announcement says which it is showing.
- **A limit that bites says so.** A table caps at 200 rows and announces the real total. The cap and
  §7.6's "virtualization stays off" are one decision: every row shown is a row built.

Two more since 1.1, because most servers publish no structured content at all and send JSON as text:

- **Text that is JSON is read as JSON** (`JsonText`): an object or array that runs to the end, with
  at most three short title lines in front. Anything less certain stays text; F1 always has the
  text as sent. Same value as the structured content means one result, compared as values.
- **An object of equal-length lists of plain values is a table**, one row per position — a
  forecast's hours and temperatures, side by side.

Shape decides the control and nothing else does — array of objects → `DataGrid`, anything else
structured → tree, long string → a window of text, and <kbd>Enter</kbd> anywhere opens what the
caret is on in a window of that kind. There is no per-server branch anywhere in the renderer.

## The two shell rules

Saving or opening a result is the only place humcp asks Windows to choose a program, and both
inputs — the bytes and the media type — come from a third party. Both rules live in `Humcp.Core`
because a security decision that can only be tested by opening a window will not be tested.

- **`MediaTypes`: the extension is ours, never the server's.** An allow-list of media types to
  extensions; anything else is saved as `.bin` and never opened. `image/svg+xml` saves as `.svg` and
  is deliberately not openable — it is an image everywhere else and a scriptable document here.
- **`LinkPolicy`: two schemes.** `http`/`https` go to the browser; a `file:` URI is *revealed* in a
  folder, never launched; everything else is copyable and inert **and says so**. `ms-settings:`,
  `vscode://` and a `file:` URI ending in `.exe` are the same attack.

## The schema engine: never fail to render

Three tiers (§5), degrading rather than failing:

1. **Direct** — primitives, enums, arrays of primitives → native controls. Strict superset of the
   elicitation subset, so elicitation forms always render natively.
2. **Composite** — objects → `GroupBox`; arrays of objects → sub-forms; `$ref` resolved with cycle
   detection (a cycle demotes to Tier 3).
3. **Raw** — `oneOf`/`anyOf`/`allOf`/`if`-`then`-`else`/unknown → multiline JSON editor with live
   validation. **Every Tier 3 field carries a user-facing reason** ("a choice between 2 different
   shapes") — §7.18, and it is also §5's telemetry.

**A nullable value is one field, not a union.** `type: ["string","null"]` and
`anyOf: [X, {"type":"null"}]` both stay Tier 1 with `AllowsNull` set. The C# SDK marks *every*
optional parameter the second way, so treating it as a union would send every optional parameter of
every C# MCP server to a JSON editor. Do not "fix" this by reading the Tier 3 list literally.

**An elicitation form hides nothing, and that is not a style choice.** The omission rule above is
humcp's and it survives inside `Humcp.Schema` — but the client SDK writes every schema-stated
`default` back into an elicitation answer on its way to the wire. humcp sends `{"plain":"typed"}`
and `{"plain":"typed","count":7,"agreed":true,"size":"medium"}` arrives at the server. Carrying the
tool form's optional-field toggle into a question window would therefore report values the user was
never shown as values they chose. A field that will be sent whether or not it is on screen must be
on screen (§7.31, phase5-notes 3.3).

**Validate client-side, but never block submission on client validation alone.** The server is the
authority; an over-strict client makes working tools unusable. Two rules make that safe:

- **Validation runs against the original schema, never `FormBuilder`'s simplified node.** The
  builder merges `allOf`, unwraps nullable unions and infers missing types — all approximations for
  choosing a control. Keeping them away from the validator means a rendering convenience can never
  change what counts as a valid call.
- **Evaluation never reaches the network.** The validator uses its own `SchemaRegistry` with `Fetch`
  pinned shut, and `SchemaResolver` refuses non-local `$ref`s. A schema is a third-party document;
  opening a form must not become an outbound request to an address a server chose.

## Accessibility invariants

These are the rules generated WPF code most often violates. Full contract in §7.

- **prism is only for text with no on-screen representation at all** (§7's core rule, rewritten
  after Phase 1). Anything that can be shown somewhere goes there — a polite live region carries
  completions, error counts and connection state, and NVDA reads it unaided. Measured by ear
  (phase1-notes 2.7): speaking the status line through prism *as well* announced every tool run
  twice. Today nothing in the app speaks through prism; the seam exists for what Phase 6 finds
  genuinely off-screen, such as an event arriving while humcp is in the background.
- **A live region must not carry a static `AutomationProperties.Name`** — a `TextBlock`'s name *is*
  its text, so naming it makes every announcement say the label instead of the message.
- **Text results go in a read-only multiline `TextBox`, never a `TextBlock`.** A `TextBox` gives
  caret navigation, so output can be reviewed line by line.
- **Disable UI virtualization on generated forms** (`VirtualizingStackPanel.IsVirtualizing=False`).
  Virtualized fields are absent from the UIA tree and silently break tab order and field counts.
  Measured in Phase 0: with 209 fields, virtualization on left **14** visible to UIA. (Note the
  tension with v4's large-document trees, which *will* need virtualization — §11.)
- **A generated form is not an `ItemsControl` as WPF ships it** (§7.12–13). Untouched, it reports
  to UIA as a `List` whose items are `DataItem`s named after the view-model's `ToString()`, and the
  container is its own unnamed tab stop. Both need a custom `ItemsControlAutomationPeer` reporting
  `Group` and `Focusable="False"`.
- **Nothing but real controls in the tab order** (§7.14). WPF tab-stops menu bars and
  `GridSplitter`s by default; a menu bar is reached with Alt, not Tab.
- **If a control has a keystroke nothing announces, the renderer puts it in `HelpText`** (§7.15) —
  a `DatePicker` opens its calendar with Alt+Down Arrow and no screen reader mentions it. Spell
  keystrokes out ("Alt plus Down Arrow"); the text is spoken.
- **Nothing is signalled by colour alone** (§7.20). A validation problem goes into `ItemStatus`, into
  words under the control, into the error summary, and is marked visually with weight rather than
  hue. Half the audience has some sight and is using it.
- **An access key is unique or absent** (§7.21). Windows cycles focus between controls sharing one,
  so a duplicate on a 23-field form sends Alt+S to four unrelated boxes. `AccessKeys.Assign` gives
  up rather than duplicating.
- **Focus after a failed submit goes to the first invalid field in *tab order*** (§7.22), which is
  not the first issue `ValidationReport` lists — it sorts alphabetically within a depth.
- **Anything bound into an items control needs a `ToString` worth hearing** (§7.23), or an explicit
  item name. `DisplayMemberPath` fixes the screen and says nothing to UI Automation — a drop-down of
  records announced "ChoiceOption { Label = Titles, Value = …" on every option. §7.12 one layer down.
- **A property being set is not the same fact as a property being announced** (§7.24). NVDA does not
  speak `ItemStatus` on focus, so a validation message that a UI test asserted was present was never
  heard by anyone. Only listening tells the two apart.
- **The caret survives whatever the keyboard does to the form** (§7.25). Rebuilding the field list
  destroys the focused control and WPF drops focus to the window; put it back. Focusing a field that
  turns out to be a group means focusing the group's first member, never giving up quietly.
- **Everything the platform gives a control of that shape, ours has too** (§7.26). WPF's `TreeView`
  has no first-letter navigation and every other list-shaped control on Windows does, so the tree
  implements it by hand.
- **A button bound to a `RoutedUICommand` announces that command's `Text` as its keyboard shortcut**
  (§7.27). WPF uses it as the `AcceleratorKey` fallback, so "Save as" is read out in the slot a
  reader uses for the shortcut. Commands carry no `Text`; buttons with a real keystroke set
  `AutomationProperties.AcceleratorKey` themselves, spelled out ("Control plus Enter").
- **Every part of a result needs one focusable control that says what it is** (§7.28). A heading and
  an explanation in `TextBlock`s are on screen and in nobody's ear — for a whole pass an image
  announced itself only through its Save button.
- **A control that navigates with arrows sets its own current position on focus** (§7.29). A grid
  with no current cell does not handle an arrow key at all, so WPF moves focus out of it: 200 rows
  on screen, none reachable, nothing announced. A list box in Phase 3, a grid in Phase 4.
- **A `Focus()` return value is not a report about where the caret went** (§7.30).
  `TreeView.Focus()` answers false while moving focus into the tree, which silently took the tree
  out of the F6 pane cycle.
- **A field that will be sent whether or not it is on screen must be on screen** (§7.31). Hiding is
  only honest where hiding also means omitting — true of a tool form, false of an elicitation form.
- **A property set on a composite is not what the part that takes focus reports** (§7.32). A
  `DatePicker` focuses a text box inside itself, which inherits the name and help text and neither
  `IsRequiredForForm` nor the access key. Set them where the focus lands; where they cannot be got
  there, spell the keystroke out (§7.15).
- **A live region is announced by its text changing, not by the event you raise.** There is no quiet
  `SetStatus`; a message that must not be spoken must not be a status message.
- **A disabled control is absent from the tab order, not merely inert.** A command with no binding,
  or one whose `CanExecute` has not been re-asked since the value that enables it changed, is a
  button nobody can reach — while its help text goes on telling them to press it.
- **A list selects what it focuses**, or `Delete` and every other "selected entry" command is dead.
- **A menu item's check mark needs `IsCheckable`** to exist as a state a reader can read.
- **`Button.IsCancel` closes a modal dialog and nothing else.** Every window humcp opens is
  non-modal, so closing is a command bound to Escape and Ctrl+W, never `IsCancel`.
- **An owned window is a child of its owner in the UIA tree**, so automation ids must be unique
  across the process, and `GetAllTopLevelWindows` will not find a form (§9).
- **No WebView2 or `WebBrowser` anywhere.**
- Every interactive control needs a non-empty `AutomationProperties.Name`, plus `HelpText` and a
  real `Label` via `LabeledBy`. Required fields use `IsRequiredForForm` — never append "(required)"
  to the name.
- Validation errors surface via `AutomationProperties.ItemStatus` **and** an error summary list;
  WPF's red-border `ErrorTemplate` is invisible to screen readers.
- All server-supplied text is untrusted and heading for a speech synthesizer: strip control
  characters, collapse whitespace, cap length. Tool descriptions are written for LLMs and are often
  enormous — first sentence to `HelpText`, full text on demand.

`tests/Humcp.App.UiTests` enforces the mechanical parts by walking the live UIA tree with FlaUI.
Assert tree depth from nested `TreeItem` elements — WPF does not implement the UIA `Level`
property. Assert the tab order in *both* directions: complete, and containing nothing else. Never
send synthetic keystrokes without first checking the app owns the foreground.

## v3 security invariant

When OAuth lands, the `client_id` metadata document URL is user-configurable (§11.3), which makes
it attacker-influenceable. The rule that closes the hole:

> humcp constructs its own loopback redirect URI and **only** verifies the document contains it.
> A non-loopback redirect URI in the document is never adopted, only rejected.

Related: never request an ephemeral port (§11.2) — CIMD requires exact redirect-URI matching while
RFC 8252 requires port flexibility, and not every server implements the latter. Bind `127.0.0.1`
only, even though the document lists `localhost` forms for compatibility.

## Commands

The solution is `Humcp.slnx` (the new format — the .NET 10 toolchain reads it natively).

```bash
dotnet build
dotnet test                                               # all three suites
dotnet test tests/Humcp.Schema.Tests                      # one project
dotnet test --filter "FullyQualifiedName~TabOrder"        # one test or fixture
dotnet run --project src/Humcp.App

HUMCP_UPDATE_GOLDEN=1 dotnet test tests/Humcp.Schema.Tests   # rewrite goldens, then read the diff
dotnet run --project tools/Humcp.Harvester                   # re-harvest the whole corpus
dotnet run --project tools/Humcp.Harvester -- playwright     # or just some of it
```

**The harvester downloads and runs ~26 third-party MCP servers over `npx` and `uvx`.** It takes a
few minutes cold and rewrites `tests/fixtures/schemas/`. Do not run it casually, and re-run the
golden tests afterwards — the corpus changing is the point, but it changes the metric too.

**`dotnet test` takes over the desktop.** `tests/Humcp.App.UiTests` launches the real app, takes
the foreground and sends real keystrokes to it. That is deliberate (§9), and the suite refuses to
type unless the app owns the foreground — but do not run it while the user is working, and never
add a test that types without calling `Input.EnsureForeground` first. When it does refuse, the
failure says so in the message; it is an environment condition, not a defect.

Drive *setup* through UI Automation patterns (`Invoke`, `Select`, `Toggle`), which need no
foreground. Keystrokes are for the assertions that are about the keyboard. It also runs real tools
from real forms, so `HUMCP_HISTORY` points MRU history at a temporary directory — a test run must
never write into the profile of whoever ran it.

Since Phase 6 the app has **no built-in server**: it connects to whatever `%APPDATA%\humcp\servers.json`
names, in the `mcpServers` shape every other client uses. A first run shows an empty tree that says
so. Add servers with <kbd>Ctrl+N</kbd>, or paste another client's block into File, Import servers.
To try a real server without touching your own list, point the app at a scratch file:

```bash
set HUMCP_CONFIG=%TEMP%\humcp-try\servers.json
set HUMCP_SETTINGS=%TEMP%\humcp-try\settings.json
set HUMCP_HISTORY=%TEMP%\humcp-try\history.json
dotnet run --project src/Humcp.App
```

All three files live in `%APPDATA%\humcp\` by default, and the UI suite redirects all three into a
temporary directory: a test run must neither read nor write the profile of whoever ran it. The
in-repo fixture is found by `tests/Shared/TestServerLocator.cs`, which both test projects compile
in; nothing under `src/` knows it exists.

## Testing notes

- **`tests/fixtures/results/` holds real tool results** captured from published servers (1.1),
  under the corpus's rule: never hand-written, captured again rather than edited. Its README says
  how each server was started.
- **The schema corpus is the highest-value test asset** (§9): 213 real `inputSchema` documents from
  22 published MCP servers in `tests/fixtures/schemas/`, with golden tests over the resulting
  `FieldModel`. Its whole value is that nobody here chose what is in it — never hand-write a fixture
  into it. Regenerate with `dotnet run --project tools/Humcp.Harvester` (optionally naming server
  ids); `manifest.json` records the servers that refused and why, on purpose.
- **Golden files are readable transcripts, one per server.** Regenerate with
  `HUMCP_UPDATE_GOLDEN=1 dotnet test tests/Humcp.Schema.Tests`, which rewrites them **and still
  fails**, so a regeneration cannot pass unreviewed. Read the diff. `coverage.txt` carries the
  headline metric and is regenerated with them.
- **The fixture server publishes 31 tools.** Seven were added in Phase 4 with the `result_` prefix,
  one per shape a result can have, and an eighth in 1.1, `result_json_text`, which sends JSON as
  text the way most real servers do; seven more in Phase 5 with the `mrtr_` prefix, one per shape a
  round trip can have; and in Phase 6 `tools_toggle`, which adds or removes `tools_appeared` so the
  client is told its list changed, and `demo_progress`, which reports progress five times. A test
  that calls `tools_toggle` on a shared server must call it again before it finishes.
- The fake in-process MCP server should exercise hostile input deliberately — 10,000-character
  descriptions, control characters, deeply nested schemas — since all of it is untrusted. Two of its
  fourteen tools exist for the form generator: `demo_search` has fifteen parameters, two required,
  one of them an enum, and covers every control the renderer knows; `demo_wide` has forty, and is
  the virtualization guard.
- **A cycle asserted at one end is not asserted.** The F6 test looked for the result pane and passed
  for a whole phase while the tree had dropped out of the cycle.
- **A `DataGrid` refuses `SetFocus` through UI Automation**, exactly as a `TreeView` does. **A bare
  `ContentControl` has no automation peer at all**, so a view built on one cannot be scoped to in a
  test — `ResultView` reports itself as an unnamed group for that reason.
- **Prefix every named element in a window that is not the main one.** §9's "automation ids are
  unique across the process", learned about two buttons in Phase 3 and re-learned twice in Phase 4
  (`MainResults`/`FormResults`, `ResultHeading`/`FormResultHeading`). With a form open, a lookup
  from the main window finds the form's element, every wait times out at thirty seconds, and the
  suite looks hung rather than wrong.
- **A test that looks a control up by name can find its `Label` instead** — §7.3 wants both to carry
  the name. Filter to focusable elements, and for a composite take the *innermost* match: a
  `DatePicker` and its inner text box both answer to the field's name and only one ever has focus.
- **Drive setup through UI Automation patterns, keystrokes only for assertions about the keyboard.**
  A shared helper that typed made every test in a file fail together whenever something else on the
  desktop held focus.
- **Collapse what you expand.** `ComboBox.Select` opens the drop-down, and a WPF popup left open
  swallows `Window.Close()` — so the window stays, the call it was waiting on stays in flight, and
  the *next* test fails on a button that never enables. The symptom is one test later than the cause.
- Automated UIA assertions prove names exist; only manual NVDA testing proves the app
  is pleasant. Do both at each phase exit. **Phase 3 found nine defects by ear that its 886 green
  tests had missed, Phase 4 found eight that its 1,007 had — plus a ninth while re-checking the
  fixes — and Phase 5 found ten that its 1,042 had, including the tenth while re-checking.** Three
  phases running, the manual pass has out-found the suite and the count is going up; budget for it
  rather than hoping.
