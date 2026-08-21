# humcp — Accessible MCP Client

**Generate real, native-feeling, screen-reader-first Windows UI automatically from any MCP server's published metadata.**

Status: Phase 1 complete — end-to-end round trip, 81 automated tests, manual NVDA pass green.
Phase 2 (the schema engine) is next.
Findings: [`phase0-findings.md`](phase0-findings.md), [`phase1-notes.md`](phase1-notes.md).

---

## 1. The thesis

MCP is accidentally an accessibility API.

To make tools usable by language models, MCP requires every server to publish machine-readable,
self-describing capability metadata: a human-readable title, a description, a JSON Schema for
inputs, an optional JSON Schema for outputs, and behavioural annotations. That is precisely the
input a UI generator needs, and precisely what desktop applications normally refuse to expose.

So: point the app at a set of MCP servers, and it builds an accessible GUI for all of them. No
per-server work, no bespoke integration. Every new MCP server published anywhere in the world
becomes a usable accessible Windows application for free.

The audience is blind and low-vision users first. The design happens to produce a fast,
keyboard-driven, no-nonsense MCP client that sighted power users would also want — but where the
two goals conflict, accessibility wins.

---

## 2. Confirmed decisions

| Decision | Choice | Rationale |
|---|---|---|
| Language / runtime | C# on .NET 10 (`net10.0-windows`) | Matches both dependencies; current LTS-track runtime |
| UI framework | **WPF** | Mature UIA implementation, ~20 years of screen-reader hardening; strong dynamic-UI story via `DataTemplateSelector`. See §2.1. **Confirmed by the Phase 0 spike** |
| Speech | **prism** via the `prismatoid` NuGet package | Announcements with nowhere on screen to live — a narrower job than it first looked, see §7. Phase 1 measured a live region covering everything visible, leaving prism idle for now |
| Transports (v1) | stdio + **unauthenticated** Streamable HTTP | OAuth deferred; keeps v1 shippable |
| LLM | **None in v1**, seam preserved | Human is the intelligence; `IResultSummarizer` / `IToolSuggester` interfaces ship as null implementations |
| Protocol SDK | `ModelContextProtocol.Core` 2.2.0 | Implements spec 2026-07-28, negotiates back to older servers |

### 2.1 Why not WinUI 3

The original proposal named WinUI 3 on the premise that it yields "real, OS-native controls." It
does not. WinUI 3 controls are custom-drawn XAML composited via DirectComposition; they expose UI
Automation providers, which is a *reimplementation* of accessibility rather than the long-hardened
Win32 path. Two findings decided it:

- [microsoft-ui-xaml#10483](https://github.com/microsoft/microsoft-ui-xaml/issues/10483) — WinUI 3's
  `TreeView` announces **every node at level 1** to NVDA, flattening the hierarchy. Open since
  April 2025, unassigned, no workaround. The tree is this app's primary navigation surface.
- [microsoft-ui-xaml#8058](https://github.com/microsoft/microsoft-ui-xaml/issues/8058) — WebView2 is
  effectively unusable with screen readers inside WinUI 3 apps.

WPF is not native either, but its UIA layer has been fixed against real screen readers for two
decades. WinForms (genuinely native `SysTreeView32` etc.) was the fallback if WPF disappointed in
the Phase 0 spike.

**It did not.** The spike put a four-level tree in front of a screen reader and the whole hierarchy
survived into the UIA tree — 3 nodes at depth 1, 4 at depth 2, 10 at depth 3, 8 at depth 4, with
per-node status read on arrow-through. That is precisely the failure WinUI 3 could not avoid.
WinForms is off the table; the fallback is spent. The six defects the spike did find were WPF
defaults doing the wrong thing in a line or two of XAML, and each one is now a rule in §7 or §9.

One caveat carried forward: **WPF does not implement the UIA `Level` property** (30154) on
`TreeViewItem`. Screen readers derive depth from nesting, which is correct and present, but tests
must assert structure rather than `Level` (§9), and v4's virtualized tree will need a custom peer
if it ever wants `Level`/`PositionInSet`/`SizeOfSet` stated explicitly (§11).

---

## 3. What MCP gives us

From `tools/list` (spec revision 2026-07-28):

| MCP field | UI use |
|---|---|
| `name` | Stable identity, disambiguation key |
| `title` | Tree node label (human-readable, distinct from `name`) |
| `description` | Accessible description / help text |
| `inputSchema` (JSON Schema 2020-12) | Control type, label, validation, required-ness |
| `outputSchema` | Structured result rendering — a real data grid, not a text blob |
| `annotations.readOnlyHint` | Skip the confirmation prompt |
| `annotations.destructiveHint` | Require confirmation; mark the node |
| `annotations.idempotentHint` | Enable a safe "run again" action |
| `icons` | Ignored |

Two capabilities are better than expected:

**Elicitation defines a spec-blessed easy path.** `elicitation/create`'s `requestedSchema` is
deliberately restricted to *flat objects of primitives* — string, number, integer, boolean, enum —
with `title`, `description`, `format` (`email`/`uri`/`date`/`date-time`), `minLength`/`maxLength`,
`minimum`/`maximum`, and `enumNames` for display labels. The spec authors already did the
"make schemas form-renderable" design work. Our Tier 1 renderer is exactly this subset, so it is
guaranteed to cover 100% of elicitation requests.

**MRTR is a GUI-shaped protocol.** In 2026-07-28, server-initiated requests were replaced by
Multi Round-Trip Requests: a `tools/call` may return `resultType: "input_required"` carrying
`inputRequests` and an opaque `requestState`. The client gathers input, then **retries the original
call** with `inputResponses` + the echoed `requestState` and a *new* JSON-RPC id. This is literally
"show the user this form, then call me again" — synchronous with the call rather than an
out-of-band interrupt, which is far easier to present coherently to a screen reader user.

---

## 4. Pitfalls and mitigations

| # | Pitfall | Mitigation |
|---|---|---|
| 1 | **Descriptions are written for LLMs.** Multi-paragraph, embedded JSON, few-shot examples, "IMPORTANT: always call X first." Unusable as an accessible description. | First sentence → `HelpText`. Full text on demand via <kbd>F1</kbd> in a Details pane. Collapse whitespace, cap at a configured length. |
| 2 | **Schema keyword sprawl.** 2026-07-28 *loosened* `inputSchema` to permit any JSON Schema 2020-12 keyword — `$ref`, `oneOf`/`anyOf`/`allOf`, `if`/`then`/`else`. Cannot be fully generalised. | Tiered renderer that degrades rather than fails (§5). **Invariant: never fail to render a tool.** |
| 3 | **No argument autocomplete for tools.** `completion/complete` supports only `ref/prompt` and `ref/resource`. There is no `ref/tool`. | Per-`(server, tool, parameter)` MRU history persisted locally, surfaced as a combo box drop-down. Offer `resources/list` URIs as suggestions where a parameter looks URI-shaped. |
| 4 | **The "method dispatch" tool pattern.** Real servers ship tools like GitHub's `pull_request_read` — a `method` enum of 9 values where which *other* parameters matter depends on the choice, stated only in prose. A naive form shows 15 fields, 12 irrelevant. Sighted users skim past; screen reader users arrow through every one. | Heuristic: if a required enum parameter exists, present it first in a wizard step. Plus a global "hide optional fields" toggle, default on. |
| 5 | **All server text is untrusted.** The spec says so explicitly for annotations. Descriptions, titles, and enum values are third-party strings heading for a speech synthesiser. | Strip control characters, normalise newlines, hard-cap lengths before anything reaches UIA or prism. Never treat `destructiveHint: false` as proof of safety. |
| 6 | **Images have no alt text.** `ImageContent` has only `data` and `mimeType`. A tool returning a chart gives the user nothing. | Announce as an object — "Image, PNG, 45 KB" — with Save and Open-in-default-app actions. Always prefer `structuredContent` when present. Optional v2: LLM-generated alt text via the `IResultSummarizer` seam. |
| 7 | **`prismatoid` is brand new.** v0.4.0, published 2026-08-12, 82 downloads. | Pin the exact version. Wrap behind our own `IAnnouncer`. Ship a no-op implementation so the app is fully functional if prism fails to load. **Phase 0:** loads, detects a running NVDA and prefers it (priority 103), `Speak()` is asynchronous so it is UI-thread-safe, braille accepted. Three rough edges — a screen reader backend exposes **no voices** (`CurrentVoice` is null, so the settings UI must not assume a voice list), native code writes to stderr past `PrismLog`, and `PrismBackendId.Uia` fails to acquire with `INVALID_PARAM` so Narrator detection is unproven. Availability callbacks arrive on a background thread and must be marshalled before touching UI |
| 8 | **Protocol churn.** 2026-07-28 is the largest revision since launch: no `initialize` handshake, no sessions, MRTR replaces server-initiated requests, Roots/Sampling/Logging deprecated. Many deployed servers still speak 2025-06-18 or 2025-11-25. | Never hand-roll the protocol. Let SDK 2.2.0 negotiate. Surface the negotiated version per server in the UI so failures are diagnosable. **Confirmed in Phase 1:** 2026-07-28 against our fixture, 2025-11-25 against `@modelcontextprotocol/server-everything`, identical client code, and the negotiated version now reads out on the server's tree node. |
| 9 | **Long-running tools.** Tasks moved to an extension; progress arrives as `notifications/progress` on the request's own response stream. | Non-modal execution with a cancel action. Progress into the status live region on a rate limit (no more than one update per ~2s). Never block the UI thread. |
| 10 | **A zero-argument call is not an argument-less request.** Omitting the `arguments` member sends a different wire shape from sending an empty object, and servers validating their input reject it: `@modelcontextprotocol/server-everything` answers "expected object, received undefined". | Always send `"arguments": {}`. Found in Phase 1 only by pointing the client at a real third-party server, which is the argument for keeping that path easy to take (§10). |

---

## 5. The schema engine

This is the core intellectual property of the project, and it is **pure logic with no UI
dependency** — therefore fully unit-testable and the right thing to build first.

`Humcp.Schema` transforms a JSON Schema into a `FieldModel` tree: a UI-agnostic description of
fields, labels, help text, constraints, and validation rules. WPF then binds templates to it.

### Tiers

**Tier 1 — Direct.** One schema node maps to exactly one native control.

| Schema | Control |
|---|---|
| `string` | `TextBox` |
| `string` + `enum` | `ComboBox` (`enumNames` for display, `enum` for value) |
| `string` + `format: date` / `date-time` | `DatePicker` + time field — with the Alt+Down Arrow hint of §7.15 |
| `string` + `maxLength > 200`, or `\n` in default | multiline `TextBox` |
| `number` / `integer` | `TextBox` with numeric validation (not a spinner — worse caret review) |
| `boolean` | `CheckBox` |
| `array` of primitives | List editor: `ListBox` + Add / Edit / Remove buttons |

Tier 1 is a strict superset of the elicitation subset, so elicitation forms always render natively.

**Tier 2 — Composite.** Recursive but still fully typed.

- `object` → `GroupBox` (a real UIA group) containing nested fields, collapsible
- `array` of `object` → item list where each row opens a nested sub-form window
- `$ref` → resolved against `$defs` with cycle detection; a cycle demotes the node to Tier 3

**Tier 3 — Raw.** Anything not representable: `oneOf`, `anyOf`, `allOf`, `if`/`then`/`else`,
`$ref` cycles, unrecognised constructs.

Render a multiline JSON `TextBox` seeded with a schema-derived skeleton, validated live against
the schema, with errors in a navigable list. Ugly, but honest and never a dead end. Tier 3 usage
is telemetry for what to promote to Tier 2 later.

### Validation

Validate client-side against the schema before sending, but **never block submission on a
validation failure alone** — servers are the authority, and an over-strict client renders a
working tool unusable. Warn, list errors, let the user override.

---

## 6. Architecture

```
Humcp.slnx
├── src/
│   ├── Humcp.Schema/      JSON Schema -> FieldModel. Pure. No UI, no MCP types.
│   ├── Humcp.Core/        Server registry, connection lifecycle, tool catalogue,
│   │                      MRTR orchestration, MRU history. No UI.
│   ├── Humcp.Speech/      IAnnouncer over prismatoid; no-op fallback.
│   └── Humcp.App/         WPF. Views, templates, view-models, window manager.
└── tests/
    ├── Humcp.TestServer/     Fake MCP server, spoken to over a real stdio pipe
    ├── Humcp.Schema.Tests/   Golden tests over a real-world schema corpus
    ├── Humcp.Core.Tests/     The catalogue and the round trip, against the fixture
    └── Humcp.App.UiTests/    FlaUI UIA-tree assertions (§9)
```

Dependency rule: `Humcp.App` → `Humcp.Core` → `Humcp.Schema`. Nothing points back. `Humcp.Schema`
does not reference the MCP SDK at all — it takes a `JsonElement` and returns a `FieldModel`.

### LLM seam

Defined in v1 as interfaces with null implementations, so v2 can add capability without
restructuring:

- `IResultSummarizer` — summarise a large result; generate image alt text
- `IToolSuggester` — natural-language "what do you want to do?" → ranked tool list

### 6.0 Layout decisions taken in Phase 1

Small, and annoying to reverse later, so they are written down rather than left in the build files.

- **The solution is `Humcp.slnx`**, the new format. The .NET 10 toolchain reads it natively and the
  Phase 0 spike already used it.
- **`Humcp.Schema` and `Humcp.Core` target `net10.0`**, not `net10.0-windows`. Neither needs
  Windows, and the target framework is what makes "pure" checkable rather than merely intended.
  `Humcp.Speech` and `Humcp.App` are `net10.0-windows`.
- **`Humcp.Speech` references `Humcp.Schema`** for `TextSanitizer` alone. Text heading for a
  synthesiser needs the same hygiene as text heading for UIA, and one implementation beats two. The
  rule that matters is intact: nothing points back at `Core` or `App`, and `Schema` stays pure.
- **`Humcp.App` references neither the MCP SDK nor anything below `Humcp.Core`.** Everything
  crossing that boundary is a neutral model, so the §6.1 hedges are structural rather than
  aspirational.
- **The fake server is a real process.** `tests/Humcp.TestServer` is a stdio MCP server the test
  suites talk to over a real pipe. A fake sharing our own serialiser would prove much less — see
  pitfall 10 for what only a real one catches.
- **Phase 1's hard-coded server is overridable from the environment**
  (`HUMCP_STDIO_COMMAND` / `_ARGS` / `_NAME`) until configuration arrives in Phase 6, so pointing
  the app at a real server never requires a code edit.

### 6.1 Portability hedges for v4 (OpenAPI)

Five rules that cost nothing in v1 and preserve the option of a second capability source later.
They are deliberately *not* an abstraction — see §12 for why the `ICapabilityProvider` interface
should not be invented until there is a second implementation to validate it against.

1. **`Humcp.Schema` references neither the MCP SDK nor WPF.** `JsonElement` in, `FieldModel` out.
   (Already the plan; this is the rule that matters most.)
2. **No MCP vocabulary in `FieldModel`.** No `McpToolName`, no `ToolAnnotations`. Neutral names
   throughout, so the model reads correctly for a non-MCP source.
3. **`FieldModel` carries an opaque `object? Binding` slot.** An adapter can record "this field
   goes in the URL path" or "this one is a header" without touching the model. One property today;
   it is what makes OpenAPI's five parameter destinations tractable later.
4. **Model safety as a neutral `OperationSafety` record** (`IsReadOnly`, `IsDestructive`,
   `IsIdempotent`) rather than passing MCP's `ToolAnnotations` around. OpenAPI derives the same
   record from the HTTP verb — more reliably than MCP does (§12).
5. **The result renderer takes `(schema, value)`, not `CallToolResult`.**

---

## 7. The accessibility contract

Non-negotiable invariants. Every generated control must satisfy all of these; the UI test suite
(§9) enforces the mechanical ones.

**The core rule: prism is only for text with no on-screen representation at all.**

The screen reader reads controls through normal UI Automation, and that includes a polite live
region — which is how completions, error counts and connection states reach the user. Anything that
can be shown somewhere goes there, and the screen reader announces it. prism is the channel of last
resort, for the case where there is genuinely nowhere to put the text.

This is narrower than it first looked. The original rule read "UIA does the nouns, prism does the
verbs", with "Tool call complete, 3 results" and "5 validation errors" as prism's work — and Phase
1's manual pass found the consequence by ear: NVDA reads the live region *and* prism speaks it, so
every tool run is announced twice (phase1-notes 2.7). A live region is UI Automation, and it
carries verbs perfectly well. Speaking anything visible — a control's contents, a status message,
the name of the pane focus just moved to — is the classic failure mode with a library like prism:
every user hears everything twice.

As of Phase 1 nothing in the app speaks through prism. The seam stays because pitfall 7 wants it
and the diagnostics view proves it loads; the open candidate for it is an event arriving while
humcp is not the foreground window, where no live region will be read (§13).

### Per-control requirements

1. `AutomationProperties.Name` — set on every interactive control, from `title ?? name`, humanised
   (`snake_case` and `camelCase` → words). Never empty.
2. `AutomationProperties.HelpText` — first sentence of `description`, sanitised and length-capped.
3. `AutomationProperties.LabeledBy` — points at a real `Label` with an access key. A visible label
   *and* a programmatic association; not one or the other.
4. `AutomationProperties.IsRequiredForForm` — for required fields. Do **not** append "(required)"
   to the name; let the screen reader announce it in the user's preferred way.
5. Validation errors surface via `AutomationProperties.ItemStatus` on the field (WPF's red-border
   `Validation.ErrorTemplate` is invisible to screen readers on its own) **and** in a navigable
   error summary list.

### Per-window requirements

6. **Disable UI virtualization on generated forms** (`VirtualizingStackPanel.IsVirtualizing=False`).
   Virtualized fields do not exist in the UIA tree and silently break both tab order and
   field-count announcements. Non-obvious and important.
7. Explicit tab order matching schema order. Everything reachable by keyboard; no mouse-only
   affordance anywhere in the app.
8. On submit-with-errors, focus moves to the first invalid field and prism announces the count.
9. **Text results render into a read-only multiline `TextBox`, never a `TextBlock`.** A `TextBox`
   gives caret navigation, so users can review output word-by-word and line-by-line and select
   arbitrary spans. This is a large usability difference and costs nothing.
10. **No WebView2 or `WebBrowser` anywhere.** Markdown in results is converted to native controls
    or presented as plain text.
11. Every window has a heading structure via `AutomationProperties.HeadingLevel`.

### Added by the Phase 0 spike

Four rules that generated WPF UI violates *by default* — each one was a live defect in the spike
before it was a rule. Numbered from 12 so the references to §7.4, §7.6, §7.8 and §7.9 elsewhere in
this document stay valid.

12. **A generated form needs a custom `ItemsControlAutomationPeer`.** A plain `ItemsControl`
    reports itself to UIA as a `List` and every generated field as a `DataItem` named after the
    bound object's `ToString()` — so a screen reader announces "list, 8 items" and then a C# class
    name on each field. The peer must report `Group`, with item peers reporting `Group` and an
    **empty** name, leaving the real controls as the only named things.
13. **Generated-form containers are not focusable.** `Control.Focusable` defaults to `true`, so
    the form's `ItemsControl` and every nested object group become tab stops of their own. Tab then
    lands on an unnamed group before reaching the first field.
14. **Nothing but real controls in the tab order.** This is the converse of §7.7 and equally
    binding: a menu bar is reached with <kbd>Alt</kbd> and arrowed between menus, never tabbed to,
    and a `GridSplitter` is reached from a command. WPF tab-stops both by default. Where removing
    a tab stop removes the only keyboard route to a feature, replace the route — do not leave the
    feature mouse-only.
15. **If a control has a keystroke the screen reader does not announce, the renderer says so in
    `HelpText`.** A `DatePicker`'s calendar opens with <kbd>Alt</kbd>+<kbd>Down Arrow</kbd> and
    nothing anywhere mentions it. The hint belongs to the control choice rather than the schema —
    the schema said `format: date` and has no idea what widget it became — so it is applied in the
    template, and it travels with whatever control a tier decision picks. Spell keystrokes out
    ("Alt plus Down Arrow"): the string is spoken, and screen readers differ on whether they
    announce "+" at all. Generated UI cannot assume a user recognises a widget by sight and
    remembers how it opens.

### Added by Phase 1

Four more, from [`phase1-notes.md`](phase1-notes.md). The first two are WPF defaults doing the
wrong thing again; the second two are about what the app says rather than how it is built.

16. **A live region must not carry a static `AutomationProperties.Name`** (phase1-notes 2.2). A
    `TextBlock`'s accessible name *is* its text, so naming the element "Status" makes every
    announcement say the word "Status" and never the message. Use `x:Name` — which becomes the
    automation id — when something needs to find the element.
17. **A window holding a single content control needs an explicit
    `AutomationProperties.Name`** (phase1-notes 2.3). A WPF `Window` is a `ContentControl` and its
    automation peer falls back to the plain text of its content: a details window announced ten
    thousand characters of tool description as its title.
18. **Declining an action says why, and how far away the ability is.** Pressing <kbd>Enter</kbd> on
    a tool whose form does not exist yet answers "Echo needs 1 parameter. Generated forms arrive in
    Phase 3." A UI generated from other people's metadata will always have edges it cannot handle;
    at an edge, silence is indistinguishable from a bug, and a bare refusal is worse than either.
19. **Text for review and text for speech are sanitised differently** (phase1-notes 2.4).
    Collapsing runs of whitespace is right for a name or a help string and wrong for a result:
    results arrive as JSON, tables and aligned columns, and §7.9's read-only `TextBox` is only
    worth having if what goes into it still has its shape. Two operations, not one — both strip
    control and formatting characters, only the spoken one collapses the layout.

### Keyboard map

| Key | Action |
|---|---|
| <kbd>Alt</kbd> | Menu bar |
| <kbd>F6</kbd> / <kbd>Shift+F6</kbd> | Cycle panes within a window |
| <kbd>Ctrl+1</kbd>…<kbd>9</kbd> | Switch to nth open window |
| <kbd>Ctrl+F</kbd> | Focus tree filter box |
| <kbd>Enter</kbd> (on a tool) | Open its form window |
| <kbd>F1</kbd> | Full details for the focused tool or field |
| <kbd>Ctrl+Enter</kbd> | Submit form |
| <kbd>Esc</kbd> | Close current form / result window |
| <kbd>Applications</kbd> | Context menu (Copy schema, Copy result, Run again) |

---

## 8. UI structure

### Main window — the tree

```
├─ github                      (connected · 41 tools · protocol 2026-07-28)
│  ├─ Tools
│  │  ├─ Issues                (grouped by name prefix)
│  │  │  ├─ Create issue
│  │  │  └─ List issues
│  │  └─ Pull requests
│  ├─ Prompts
│  └─ Resources
├─ filesystem                  (connected · 12 tools)
└─ postgres                    (error: process exited with code 1)
```

- Category nodes appear only when non-empty.
- Auto-grouping when a server exposes many tools sharing `.`/`_`/`-` name prefixes. Implemented in
  Phase 1, deliberately conservative: at least 8 tools, at least 2 prefixes, at least 2 tools each,
  or the list stays flat — a user arrowing through six tools should not spend two presses on group
  headers. A tool whose prefix does not earn a group sits beside the groups rather than in a
  "Miscellaneous" bucket nobody asked for. Still a guess; §13 keeps it open against the corpus.
- Server node status is part of its accessible name, so it is heard on arrow-through.
- A filter box above the tree does type-ahead over tool names, titles, and descriptions.

Windows are **non-modal** — several tool forms and results can be open at once, navigable via the
Window menu. Menu bar: File (add/remove/reconnect server, settings, exit), Edit (standard clipboard
+ Copy Schema / Copy Result as JSON), View (hide optional fields, verbosity), Window, Help.

Server configuration is stored as JSON, deliberately compatible in shape with the familiar
`mcpServers` block so existing configurations can be pasted in.

---

## 9. Testing strategy

**Schema corpus (highest-value test asset).** Harvest real `inputSchema` documents from ~20 widely
used MCP servers — GitHub, filesystem, Postgres, Slack, Puppeteer, Sentry, and others — and check
them into `tests/fixtures/schemas/`. Golden tests assert the `FieldModel` each produces. This
grounds tier coverage in what servers actually ship rather than what the spec permits, and gives a
concrete metric: *percentage of real-world parameters rendered at Tier 1*.

**Fake MCP server.** An in-process server exercising every content type, `isError`, MRTR
round-trips, progress notifications, and deliberately hostile input (10,000-character
descriptions, control characters, deeply nested schemas).

**Automated accessibility regression via FlaUI.** Walk the live UIA tree and assert mechanically:
every focusable element has a non-empty `Name`; every input has `HelpText` or `LabeledBy`; tab
order is complete and cycles. This catches the exact class of bug that sank WinUI 3's TreeView, in
our own code, on every build.

The Phase 0 probe is the seed for `tests/Humcp.App.UiTests` and settled four details of how these
assertions have to be written:

- **Assert tree depth structurally, by counting nested `TreeItem` elements — not via the UIA
  `Level` property**, which WPF does not implement (§2.1).
- **Assert the tab order in both directions**: that it reaches every field *and* that it contains
  nothing else (§7.14). The original probe only checked completeness, which is why two chrome tab
  stops survived to the manual pass.
- **Keep a virtualization guard test.** With 209 fields on a generated form, `IsVirtualizing=True`
  left 14 of them visible to UIA and `False` left all 209. That 195-field gap is the number to
  quote when someone proposes turning virtualization on for performance (§7.6).
- **Whitelist framework chrome explicitly.** WPF's own title bar, status bar and `TreeViewItem`
  expander toggles are unnamed and unreachable by keyboard. Name them in the test rather than
  loosening the rule that catches our own unnamed controls.

Never send synthetic keystrokes unless the app under test genuinely owns the foreground — check
the focused element's process first, or the test types into whatever the developer is doing.

Phase 1 built this suite and added three more mechanical details, each of which produces a *flaky*
test rather than a failing one when got wrong:

- **A `TreeView` is not focusable once it has items.** `SetFocus` on the tree itself throws; focus
  belongs to the selected `TreeViewItem`. Focus an item.
- **Disabled controls leave the tab order**, so a tab-order test must first put the window in a
  state where everything it expects to reach is enabled. Ours starts by focusing a runnable tool.
- **Taking the foreground is retried, not attempted.** Windows refuses `SetForegroundWindow` when
  the calling process is not already in front, which is the normal case while other test projects
  run alongside. Keep the refusal — never type without the foreground — but spend some seconds
  trying for it first.
- **The fake server is a separate process**, spoken to over a real pipe (§6.0). Pitfall 10 is the
  standing argument for that: a fake sharing our own serialiser agreed with us, and a real server
  did not.

The suite runs as part of `dotnet test` rather than behind a flag. It takes the foreground and
sends real keystrokes, which is the point of it.

**Manual matrix.** NVDA 2026.1, JAWS, and Narrator against the walking skeleton at each phase exit.
Automated checks prove names exist; only a human proves the app is *pleasant*.

---

## 10. Phased roadmap

Sizes are rough and relative, not commitments.

### Phase 0 — Retire the framework risk ✅ done
Throwaway WPF spike: a four-level `TreeView` plus a generated form covering every Tier 1 control
and one Tier 2 group, driven by `DataTemplateSelector` — the Phase 3 mechanism, so the spike tested
the real risk rather than a hand-built form. Plus a FlaUI probe for the mechanical assertions and a
`prismatoid` smoke test.

**Exit met: WPF confirmed.** The tree keeps its full hierarchy (§2.1); prism loads, detects NVDA
and speaks asynchronously (§4, pitfall 7). Six defects found — three by the probe, three by ear —
all of them WPF defaults rather than framework limits, and all now rules: §7.12–15 and the testing
notes in §9. `DatePicker` is accepted for `format: date`. Full record and the manual checklist,
which is the script to re-run at each later phase exit, in
[`phase0-findings.md`](phase0-findings.md).

Outstanding, neither blocking: the JAWS and Narrator passes, and a braille display test (§13).

### Phase 1 — Walking skeleton ✅ done
Solution structure. Connect to one hard-coded stdio server. `tools/list` into a real tree. Call a
zero-argument tool. Show the text result in a read-only `TextBox`.

**Exit met.** The round trip works, is driven entirely from the keyboard, and the manual NVDA pass
is green across all nineteen checks. 81 automated tests, including a FlaUI suite that walks the
live UIA tree, tabs in both directions, and runs a tool with real keystrokes.

The hard-coded server is an in-repo stdio fixture (`tests/Humcp.TestServer`), overridable from the
environment (§6.0) — which is how Phase 1 also connected to
`@modelcontextprotocol/server-everything`, negotiated **2025-11-25** against it, and found pitfall
10. Four defects found and fixed, four rules added to the contract (§7.16–19) and one rewritten
(§7's core rule, after the manual pass caught every announcement being spoken twice). Full record
in [`phase1-notes.md`](phase1-notes.md).

Outstanding, neither blocking: the JAWS and Narrator passes, still carried forward from Phase 0.

### Phase 2 — Schema engine (~1 week)
`Humcp.Schema` in full: all three tiers, `$ref` resolution with cycle detection, validation,
description sanitisation. Harvest the corpus and build golden tests. No UI work.

**Exit:** ≥90% of corpus parameters render at Tier 1 or 2; every schema produces *something*.

### Phase 3 — Form generation (~1 week)
`DataTemplateSelector` over `FieldModel`. The full accessibility contract (§7). Required/optional
toggle, the enum-first wizard heuristic, MRU history for parameter values, validation surfacing.

**Exit:** FlaUI suite green; a GitHub tool with 15 parameters is comfortable to fill in by ear.

### Phase 4 — Result rendering (~4–5 days)
`structuredContent` + `outputSchema` → `DataGrid` or property tree. Multi-part `content` arrays.
Images as objects with save/open. `resource_link` and embedded resources. `isError` handling.

**Exit:** every content type has a defined, non-degenerate accessible presentation.

### Phase 5 — MRTR and elicitation (~3–4 days)
Declare the `elicitation` capability. Handle `resultType: "input_required"`: build the form from
`requestedSchema`, honour accept/decline/cancel, echo `requestState` verbatim, retry with a new
JSON-RPC id. Decline sampling requests cleanly (deprecated, and no LLM in v1).

**Exit:** a multi-step server tool completes through the GUI.

### Phase 6 — The shell (~1 week)
Menu bar, window manager, settings, server add/remove/reconnect UI, config persistence,
`notifications/tools/list_changed` handling via `subscriptions/listen`, announcement policy with
rate limiting.

Three items carried in from earlier phases:

- **A keyboard route to resizing a pane.** `GridSplitter`s were taken out of the tab order in Phase
  0 (phase0-findings 2.4) and nothing replaced the route. Either a View-menu command that hands a
  splitter the arrow keys, or pane sizes become a setting and splitters stop being focusable.
  Silently having no route is the one option that is not allowed (§7.14).
- **Decide whether prism has a job.** As of Phase 1 nothing speaks through it, because everything
  the app says has somewhere on screen to live (§7). The candidate is an event arriving while
  humcp is not the foreground window, where no live region is read to the user — a policy question,
  and the first real one for this seam.
- **Replace the hard-coded server and the environment override** (§6.0) with real configuration,
  and drop `Humcp.App`'s build-order reference to the test fixture.

**Exit:** the app is configurable and usable without editing files by hand.

### Phase 7 — Hardening and release (~1 week)
Streamable HTTP transport (unauthenticated). Robust error and disconnect handling. Cancellation.
Untrusted-input hardening. Packaging and a signed installer. User documentation written for
screen reader users.

**Exit:** v1.0.

---

## 11. Post-v1 releases

### v2 — Depth on MCP

Activate the seams and finish the protocol surface, before widening scope.

- **LLM integration** through the existing `IResultSummarizer` / `IToolSuggester` interfaces:
  natural-language tool search, summarisation of large results, and — the highest-value one for
  this audience — **generated alt text for returned images**, which closes pitfall #6.
- **Prompts and Resources as first-class UI.** v1 lists them; v2 makes them usable. Prompts get the
  same generated-form treatment, and they are the *only* place `completion/complete` works, so
  prompt arguments get real autocomplete that tool arguments cannot have (pitfall #3).
- **The `io.modelcontextprotocol/tasks` extension** for genuinely long-running work: poll via
  `tasks/get`, feed input via `tasks/update`, with a background task list in the shell.
- **Braille output tuning** via prism, if UIA braille proves insufficient. Phase 0 established that
  the path works — `Braille()` is accepted through the NVDA backend — but not yet whether a
  separate braille presentation policy is *wanted*; that needs a session with a display connected
  (§13).

---

### v3 — Authenticated HTTPS

v1 ships unauthenticated Streamable HTTP. v3 adds OAuth 2.1 so hosted, real-world MCP servers
become usable. This is also a prerequisite for v4 in practice, since most interesting APIs are
authenticated — so the credential machinery should be built provider-neutral from the start.

#### The flow

```
MCP request without token
  → 401 + WWW-Authenticate: Bearer resource_metadata="…", scope="…"
  → GET Protected Resource Metadata            (RFC 9728, servers MUST implement)
  → select authorization server
  → GET AS metadata                            (RFC 8414 or OIDC Discovery — MUST support both)
  → obtain client_id                           (see priority order below)
  → record issuer + PKCE verifier + state
  → browser: authorize + code_challenge (S256) + resource=<canonical MCP server URI>
  → callback with code + iss
  → validate iss against recorded issuer       (RFC 9207)
  → token request + code_verifier + resource
  → Authorization: Bearer <token> on every subsequent request
```

#### Requirements worth writing down now

**Client registration priority.** Pre-registered credentials → **Client ID Metadata Documents**
(if the AS advertises `client_id_metadata_document_supported`) → Dynamic Client Registration
(deprecated, fallback only) → prompt the user to paste credentials.

**CIMD implies a hosting dependency.** A Client ID Metadata Document is a JSON file served over
HTTPS whose URL *is* the `client_id`, and whose `client_id` field must match that URL exactly.
**Resolved:** hosted on a subdomain of `interfree.ca` or `rblind.com`. See §11.2 for the details,
which are less trivial than "put a file on a web server." CIMD client IDs are portable across
authorization servers, so no re-registration when a server changes its AS.

**If we fall back to DCR**, `application_type: "native"` must be set explicitly. Omitting it
defaults to `"web"` under OIDC, which rejects loopback redirect URIs.

**The `resource` parameter (RFC 8707) is mandatory** in both authorization and token requests,
carrying the canonical MCP server URI — and must be sent *regardless of whether the AS supports
it*. This is what binds a token to one server and prevents it being replayed against another.

**`iss` validation is strict.** Compare literally against the recorded issuer — no case folding,
no default-port elision, no trailing-slash or percent-encoding normalisation. If the AS advertises
`authorization_response_iss_parameter_supported: true` and `iss` is absent, reject. On mismatch,
do not even *display* the returned `error_description`.

**Scope strategy.** Prefer the `scope` from the `WWW-Authenticate` challenge; fall back to
`scopes_supported`. On a 403 `insufficient_scope`, perform step-up re-authorization using the
**union** of previously requested and newly challenged scopes — requesting only the challenged
scope silently drops permissions the user already granted. Enforce a retry limit.

**Credential binding.** Persisted credentials are keyed by the AS `issuer` identifier, never reused
across authorization servers, and re-registered when protected resource metadata points somewhere
new. Tokens stored via DPAPI (`ProtectedData`, `CurrentUser` scope).

#### The accessibility work, which is the part nobody else does

OAuth in a desktop app means focus leaves the application entirely, goes to a browser, and comes
back — and screen reader users lose their place completely when that happens. This is where v3
earns its keep:

- **Announce the handoff before it happens** — "Opening your browser to sign in to github.com" —
  via prism, since no UIA event covers "another process is about to steal focus."
- **A non-modal waiting state with a working Cancel**, not a frozen window and not a modal dialog
  the user cannot escape.
- **The loopback landing page must itself be accessible.** The browser lands on a page *we* serve
  at `http://127.0.0.1:<port>/callback`. It needs a real `<h1>`, a clear "authorization complete,
  you can close this tab" message, and a `<title>`. Trivially easy to forget and actively hostile
  when forgotten.
- **A manual paste-the-code fallback** for when a firewall blocks the loopback listener. A broken
  browser handoff must degrade to something usable, never to a dead end.
- **Announce the outcome on return**, success or failure, with the failure reason.

#### 11.2 The Client ID Metadata Document

One document, hosted once, used by every humcp installation. The `client_id` identifies the
*application*, not the user — there is no per-user registration and nothing to provision at install
time. humcp is a **public client**: CIMD forbids `client_secret_post`, `client_secret_basic`,
`client_secret_jwt`, and any other shared-symmetric-secret method, which suits a distributed
desktop app that could never keep a secret anyway. PKCE (S256) carries the security.

**URL format.** Must be `https`, must contain a path component, no dot segments, no fragment, no
userinfo. `https://humcp.rblind.com/oauth/client.json` qualifies; a bare domain does not. The draft
recommends short, stable URLs *because they may be shown to end users*.

**The ephemeral-port problem — the one real design constraint.** Two specs disagree:

- [RFC 8252 §7.3](https://datatracker.ietf.org/doc/html/rfc8252#section-7.3): the AS **MUST** allow
  any port for loopback redirect URIs, to accommodate OS-assigned ephemeral ports.
- [CIMD draft](https://www.ietf.org/archive/id/draft-ietf-oauth-client-id-metadata-document-00.html):
  the AS must ensure the redirect URI is an **exact match** of a registered one — and the draft has
  no provisions for native apps, loopback, or ports whatsoever.

An authorization server implementing CIMD strictly, without RFC 8252's loopback exception, will
reject an OS-assigned port. Therefore: **do not request an ephemeral port.** Use a fixed allowlist
of candidate ports, tried in order until one binds — compiled in for the default document, and
read from `redirect_uris` when a custom document is configured (§11.3). Treat the published list as
**append-only**: adding ports later is a JSON redeploy needing no app update, but removing one
breaks every install still using it. Choose below the Windows ephemeral range (49152–65535) and
above the crowded low registered ports.

**List `localhost`, bind `127.0.0.1`.** List both forms so exact-match servers accept either, but
bind the listener to the `127.0.0.1` literal only. RFC 8252 marks `localhost` NOT RECOMMENDED
because binding to the hostname risks listening on non-loopback interfaces. Listing it is safe;
binding to it is not.

```json
{
  "client_id":   "https://humcp.rblind.com/oauth/client.json",
  "client_name": "humcp",
  "client_uri":  "https://humcp.rblind.com",
  "redirect_uris": [
    "http://127.0.0.1:33418/callback", "http://localhost:33418/callback",
    "http://127.0.0.1:33419/callback", "http://localhost:33419/callback",
    "http://127.0.0.1:33420/callback", "http://localhost:33420/callback",
    "http://127.0.0.1:33421/callback", "http://localhost:33421/callback"
  ],
  "grant_types":  ["authorization_code", "refresh_token"],
  "response_types": ["code"],
  "token_endpoint_auth_method": "none"
}
```

**`client_name` is the consent screen.** It is literally the string users read as "*humcp* wants
access to your account." Branding decision, not a technical one.

**Availability is now a production concern.** The AS fetches this URL during new authorizations, so
if the host is down, no user anywhere can complete a first-time sign-in — though existing tokens
keep working. Serve it from static hosting or a CDN with generous `Cache-Control`; servers cache
respecting HTTP cache headers. Keep it under the recommended 5 KB. Note that servers **MUST NOT**
cache malformed or error responses, so a bad deploy will not poison caches — but neither will it be
forgiven while it is live. Worth a smoke test in CI that fetches the live URL and asserts
`client_id` still equals it.

**Domain trust.** Authorization servers "MAY have their own heuristics and policies around the
trust of domain names used as client IDs" — the draft names warning screens for unfamiliar domains
and checks on registration recency. Established domains are an advantage; both candidates beat
registering something new for the purpose.

#### 11.3 The `client_id` URL is user-configurable

Our hosted document is the **default**, not a hardcoded constant. The URL is a setting.

**Why this matters more than it looks.** A CIMD `client_id` is fetched *by the authorization
server*, not by us — so if `humcp.rblind.com` ever goes dark, every user's first-time sign-in
breaks and there is nothing a client-side fallback can do about it. Making the URL configurable is
the only available mitigation, and it also serves three ordinary cases: an organisation that wants
its own client identity for an AS with a domain allowlist, a privacy-motivated user who would
rather not have their authorizations resolve through our domain, and local development against a
staging document.

**Ship the canonical document in the repository.** `assets/oauth/client.json` is the source of
truth, deployed to the website from there. If the project is ever abandoned, the document, its
history, and the instructions for self-hosting survive in every clone. Abandonment insurance is
only insurance if it outlives the website.

**Derive the loopback ports from the document, not from a constant.** This is the design
consequence worth getting right. A self-hoster who writes their own `redirect_uris` would otherwise
have to guess which ports humcp binds. Instead:

- **Default path:** compiled-in port list, matching the published document. No network fetch, no
  startup dependency.
- **Custom path:** fetch the configured document, parse `redirect_uris`, and bind only ports it
  actually lists.

One source of truth, and self-hosting works without touching the app.

**Security: never adopt a redirect URI from the document.** A configurable `client_id` URL is an
attack surface. If a user is talked into pointing the setting at an attacker's document, that
document controls `redirect_uris` — and a document listing `https://evil.example/callback` would
send the authorization code to the attacker. The invariant that closes this:

> humcp constructs its own loopback redirect URI and **only** verifies that the document contains
> it. A non-loopback redirect URI in the document is never adopted, only rejected.

PKCE is a strong second line here — an intercepted code is useless without the verifier — but it
is defence in depth, not the control. Additionally: this setting is changed only through the
settings UI or config file, never through a URL handler, deep link, or anything an MCP server can
influence. Changing it away from the default shows a clear warning.

**Validate on save, with a Verify action.** Fetch the configured URL and check, reporting each as
a separate item in an accessible results list rather than one opaque "invalid":

1. reachable over HTTPS, under 5 KB, parses as JSON
2. `client_id` exactly equals the document URL
3. `redirect_uris` contains at least one usable `127.0.0.1` loopback entry
4. `token_endpoint_auth_method` is not a shared-symmetric-secret method (CIMD forbids these)
5. URL format: `https`, has a path component, no dot segments, fragment, or userinfo

**Offer "Export client metadata document…"** which writes a ready-to-host `client.json` pre-filled
with correct `redirect_uris`. Self-hosting should be *copy this file to your web server*, not
*read the RFC and hope*. Pair it with a `docs/self-hosting.md` written for the person doing it at
2am because our domain expired.

**Map the failure to a recoverable message.** When an AS cannot fetch our document it will most
likely return `invalid_client` or `invalid_request` — opaque and misleading. Detect it and say what
is actually happening: *"The authorization server could not retrieve humcp's client metadata
document. If this keeps happening, you can host your own — see Help."* A dead end becomes a
recoverable state.

**Scope:** one global setting, with an optional per-server override for the enterprise case where a
single AS demands a specific client identity. Model the override in config from the start; the UI
for it can wait.

**Related:** the spec's registration priority order already puts pre-registered credentials
*above* CIMD, so a user with a fully registered OAuth client can bypass this path entirely. The
configurable URL covers the different case of wanting a custom client identity against an AS that
supports neither pre-registration nor DCR.

---

### v4 — OpenAPI

**Verdict: worth doing, but do not build the abstraction now.** See §12 for the reasoning; the
v1 hedges are in §6.1.

Scope, in dependency order:

1. **OpenAPI 3.1 first.** Its Schema Object *is* JSON Schema 2020-12, so `Humcp.Schema` applies
   unchanged. This is the cheapest possible proof that the generalisation works.
2. **Parameter assembly.** The one genuinely structural difference: MCP has a single flat
   `arguments` object, while OpenAPI splits inputs across `path`, `query`, `header`, `cookie`, and
   `requestBody`. One generated form must assemble into five destinations. This is what the
   `FieldModel.Binding` slot (§6.1) exists for.
3. **Extract `ICapabilityProvider`** — now, with two real implementations pushing on it, not before.
4. **Responses by status code.** Each status has its own schema and content types; the result
   renderer selects by response code rather than reading a single `outputSchema`.
5. **3.0 and Swagger 2.0 conversion.** `nullable: true` → `type: [x, "null"]`, boolean
   `exclusiveMinimum`/`exclusiveMaximum` → numeric form, and the rest. Well-trodden; use an
   existing converter rather than writing one.
6. **Security schemes** — `apiKey` (header/query/cookie), `http` basic/bearer, `oauth2` (4 flows),
   `openIdConnect`. Reuses the v3 credential store, which is why v3 comes first.
7. **Scale.** MCP servers expose tens of tools; Stripe, AWS, and Azure expose thousands of
   operations across megabytes of YAML. Lazy tree loading, virtualization, and search stop being
   nice-to-haves. Note that tree virtualization interacts with accessibility (`PositionInSet` /
   `SizeOfSet` reporting) in a way the form rule in §7.6 explicitly avoids — it needs its own
   FlaUI coverage. Phase 0 measured the cost of getting this wrong on a form: 195 of 209 fields
   vanished from the UIA tree. And since WPF implements neither `Level` nor set-position properties
   on `TreeViewItem` (§2.1), stating them explicitly means a custom automation peer.
8. **Content types and file upload.** `multipart/form-data` and
   `application/x-www-form-urlencoded` request bodies, and file pickers, have no MCP analogue.

---

## 12. Should the UI maker be generalised for OpenAPI?

Yes — and most of the value is already secured for free.

### What already transfers

The expensive, reusable asset is the schema engine, and the v1 plan already keeps it portable via a
single rule: **`Humcp.Schema` does not reference the MCP SDK.** It takes a `JsonElement` and returns
a `FieldModel`. Since OpenAPI 3.1's Schema Object *is* JSON Schema 2020-12 — the same dialect MCP
uses — the engine applies to OpenAPI 3.1 with essentially no changes.

Also protocol-agnostic already: the accessibility contract (§7), form generation, validation
surfacing, result rendering, MRU history, the window model, and the FlaUI test harness. That is the
overwhelming majority of the work.

### What does not transfer

Parameter locations (five destinations vs. one flat object), content-type negotiation and file
upload, responses keyed by status code, 3.0/2.0 dialect conversion, the much wider security-scheme
matrix, server URL templating, and sheer document scale. Real work, but bounded and enumerable —
which is what makes this a reasonable v4 rather than a rewrite.

### Two things OpenAPI makes *better*

**HTTP verbs give us MCP's annotations for free, and more trustworthy.** GET/HEAD/OPTIONS imply
read-only; DELETE implies destructive; PUT and DELETE imply idempotent. Unlike MCP's `annotations`
— which the spec explicitly instructs clients to treat as untrusted server-supplied hints
(pitfall #5) — the HTTP method is a structural fact about the operation. The
confirm-before-destructive logic gets *stronger* under OpenAPI, not weaker.

**`discriminator` promotes `oneOf` from Tier 3 to Tier 2.** OpenAPI's discriminated unions are
common and carry an explicit tag field, so they render beautifully: a combo box for the
discriminator that swaps the sub-form beneath it. This is exactly the method-dispatch pattern from
pitfall #4 — except machine-readable instead of buried in prose. Building it for OpenAPI gives us a
principled way to retrofit better handling of MCP's dispatch-style tools, so the benefit flows
backwards.

### The architectural rule: two implementations before an abstraction

The temptation is to design `ICapabilityProvider` now, in v1, "so OpenAPI slots in later."
Don't. Extracting an interface from a single implementation is guessing at where the joints are,
and the guesses are usually wrong in ways that cost more than the refactor would have. Build MCP
properly through v3, then extract the interface in v4 when a second implementation is actually
pushing on it. The §6.1 hedges are deliberately limited to changes that cost nothing today.

### The honest counterargument

**OpenAPI-to-MCP bridges already exist.** We could support zero OpenAPI code and tell users to run
a bridge. That is a real option and should be re-evaluated at v4 planning time rather than
dismissed. Native support wins on fidelity — bridges tend to flatten status codes, remove
content-type choice, and hide authentication behind their own configuration — and avoids a second
process in the dependency chain. But if the bridge ecosystem is good by then, v4 may be much
cheaper than this section assumes.

### Where this lands us competitively

For MCP, humcp is *the* accessible client; there is no incumbent. For OpenAPI, the neighbours are
Postman, Insomnia, and Bruno — all Electron applications, all poor with screen readers. So the
niche is genuinely underserved and the accessibility angle remains the differentiator. The risk is
scope creep into full API-client territory: environments, collections, scripting, request history.
v4 should stay firmly "browse and invoke operations accessibly" and refuse the rest.

---

## 13. Open questions

- Does prism's braille output need a separate presentation policy, or does UIA braille via the
  screen reader suffice? **Still open.** Phase 0 proved the mechanism — prism's `Braille()` and
  `Output()` are accepted through the NVDA backend, and NVDA advertises braille support — but the
  question is a judgement about presentation, not capability, and wants a session with a display
  connected. Cheap to defer: the `IAnnouncer` seam already separates `Speak`, `Braille` and
  `Output`, so answering it later changes a policy, not a structure.
- Tool grouping heuristics: prefix-splitting is a guess. Phase 1 implemented a conservative version
  (§8) and it reads well on a ten-tool server, which proves nothing about a forty-tool one. Revisit
  against the harvested corpus — it may turn out most servers don't namespace consistently enough
  for it to help, and the thresholds are three constants in one method precisely so that answer is
  cheap to act on.
- **Does prism have a job at all?** Phase 1 answered the narrow question — a live region reaches
  NVDA unaided, and speaking on top of it announces everything twice (§7) — which leaves the seam
  loaded and idle. The remaining case is an event arriving while humcp is in the background, where
  no live region will be read: a slow call finishing, a connection dropping. Whether speaking from
  a window the user is not looking at is welcome or intrusive is a judgement, not a capability, and
  it wants a session with a screen reader rather than an argument (Phase 6).
- Should Tier 3's raw JSON editor offer schema-driven completion? Valuable, but it is a text-editor
  feature and text editors are a deep well. Probably v2.
- ~~Where does the Client ID Metadata Document live?~~ **Resolved:** a subdomain of `interfree.ca`
  or `rblind.com`, canonical copy in `assets/oauth/client.json`, URL user-overridable (§11.3).
  Remaining sub-decisions, both cheap and both effectively permanent for default users: which
  domain (user-visible on consent screens) and the exact loopback port allowlist (append-only once
  shipped; self-hosters are unconstrained since they supply their own).
- Re-evaluate OpenAPI-to-MCP bridges at v4 planning time. If the ecosystem has matured, native
  OpenAPI support may be far cheaper than §11's v4 scope assumes — or unnecessary.
- Does the v3 credential store need to be provider-neutral from day one, or is a v4 refactor
  cheaper? Leaning neutral, since it is a storage schema and those are expensive to migrate.
