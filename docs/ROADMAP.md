# humcp — Accessible MCP Client

**Generate real, native-feeling, screen-reader-first Windows UI automatically from any MCP server's published metadata.**

Status: Phase 2 complete — the schema engine renders 95% of real-world parameters natively, against
a corpus harvested from 22 published MCP servers. 813 automated tests. Phase 3 (form generation) is
next. Findings: [`phase0-findings.md`](phase0-findings.md), [`phase1-notes.md`](phase1-notes.md),
[`phase2-notes.md`](phase2-notes.md).

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
| JSON Schema validation | `JsonSchema.Net` 9.4.0 | The one package `Humcp.Schema` references. Assertion semantics are a conformance surface and a hand-rolled subset would disagree with the server about what is valid; the *messages* are all ours, because they are spoken. See phase2-notes 3.6 |

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
| 4 | **The "method dispatch" tool pattern.** Real servers ship tools like GitHub's `pull_request_read` — a `method` enum of 9 values where which *other* parameters matter depends on the choice, stated only in prose. A naive form shows 15 fields, 12 irrelevant. Sighted users skim past; screen reader users arrow through every one. | A global "hide optional fields" toggle, default on — which is where the whole of the measured benefit turned out to be: 491 of the corpus's 785 top-level parameters are optional, and its two largest forms are 23 fields with one required and 23 with none. Plus focus starting on the first required field, which on a dispatch tool *is* the enum. **The original "present the required enum first in a wizard step" was measured and dropped** (phase3-notes 3.1): 13 of 213 tools have a required top-level enum and 9 of those already declare it first, so reordering would move a field on four tools while costing §7.7. |
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

**A nullable value is one field, not a union.** `type: ["string", "null"]` and
`anyOf: [{…X}, {"type": "null"}]` are both how a generator spells "X, or nothing" — the C# SDK marks
*every* optional parameter the second way. Both stay Tier 1, with the field flagged as accepting
null, and an empty box means null rather than an empty string. Written down because a reader coming
to the Tier 3 list below would otherwise correctly conclude that `anyOf` degrades, and undo it
(phase2-notes 3.2).

**Tier 2 — Composite.** Recursive but still fully typed.

- `object` → `GroupBox` (a real UIA group) containing nested fields. Not collapsible in the end: an
  expander is another tab stop and another state to announce, and the optional-field toggle already
  solves the length problem (phase3-notes §4)
- `array` of `object` → item list where each row opens a nested sub-form window
- `$ref` → resolved against `$defs` with cycle detection; a cycle demotes the node to Tier 3

A list of primitives is **Add and Remove, with no Edit** (phase3-notes 3.8). Editing an entry
through the box the next one is typed into is a mode, and a mode whose state is not announced is
the thing this application exists to avoid. If Edit is wanted later it opens a window, the way a
row of an object list already does.

**Tier 3 — Raw.** Anything not representable: `oneOf`, `anyOf`, `allOf`, `if`/`then`/`else`,
`$ref` cycles, unrecognised constructs.

Render a multiline JSON `TextBox` seeded with a schema-derived skeleton, validated live against
the schema, with errors in a navigable list. Ugly, but honest and never a dead end. Tier 3 usage
is telemetry for what to promote to Tier 2 later.

### Validation

Validate client-side against the schema before sending, but **never block submission on a
validation failure alone** — servers are the authority, and an over-strict client renders a
working tool unusable. Warn, list errors, let the user override. A schema the validator cannot even
load is reported as *unchecked*, which is a different fact from *valid* and must not be flattened
into the same green.

**Validation runs against the original schema document, never the builder's simplified one.**
`FormBuilder` merges `allOf`, unwraps nullable unions and infers a missing `type` from neighbouring
keywords, all of them approximations made to decide which control to draw. Keeping them out of the
validator is what makes them safe: the worst case becomes a box that looks more permissive than it
is and a server that says no, rather than a rendering convenience quietly changing what counts as a
valid call (phase2-notes 3.5).

**Evaluation never reaches the network.** A schema is a third-party document (§4, pitfall 5), so a
`$ref` to an attacker-chosen URL must not turn opening a form into an outbound request. The
validator's schema registry has its fetch hook pinned shut, and `$ref` resolution refuses anything
that is not local to the document (phase2-notes 3.7).

**There is one refusal, and it is not a validation failure.** "Never block" is a rule about
disagreement — we suspect the server will decline this. Text in a Tier 3 editor that is not JSON is
a different thing: there is no argument object to send. That one case refuses, says so, and moves
the caret to the box (§7.18). Everything else warns once and sends on the second press
(phase3-notes 3.6).

### The other direction: what a result means

The same engine runs backwards. `ResultBuilder.Build(schema?, value)` takes a value and produces a
`ResultNode` tree — values, groups, lists and tables — and the schema is **optional**, which the
corpus turned from a portability hedge into a requirement: 25 of its 213 tools publish an
`outputSchema` at all (phase4-notes 3.1). So the *shape* is read from the value, and the schema,
where there is one, supplies names and descriptions — which is worth having, since a server's own
`title` and `description` reach the user through a client that has never heard of that server.

Three rules came out of the corpus rather than out of taste:

- **A wrapper of one property comes off.** SEP-2106 still asks an `outputSchema` to describe an
  object, so a tool whose answer is a list publishes `{"entities": […]}` — 17 of the 25. The
  property's own name is kept and the wrapper is not drawn.
- **Structured content that repeats the text is not drawn twice.** Thirteen of the 25 are
  `{"content": "…"}` holding the string the tool already sent as text. The announcement says which
  of the two it is showing; the raw JSON stays under <kbd>F1</kbd>.
- **A limit that bites says so.** A table caps at 200 rows and reports the real total, because
  virtualization stays off (§7.6) and the two are one decision (phase4-notes 3.4).

The invariant is `FormBuilder`'s, unchanged: **there is no value it refuses to render.** Anything it
cannot take apart becomes a node holding the JSON with a sentence saying why.

### What a filled-in form means

**A value the user did not choose is not sent** (phase3-notes 3.2). A box still holding what the
schema's `default` put there says nothing; a required field is the exception, because the call has
to state it. The rule is one expression — `IsRequired || value != initial` — and the case that looks
like an exception is not: a check box that started ticked because the schema said so, and has been
unticked, differs from its default and so sends `false` out loud, since otherwise a stated default
could not be turned off.

Repeating a default back pins a value the server may have changed since it wrote its schema, and
turns "I did not express an opinion" into an opinion. Found on five real servers at once by a corpus
test, not by reasoning.

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
├── tools/
│   └── Humcp.Harvester/   Starts real MCP servers and writes down what they publish (§9)
└── tests/
    ├── Humcp.TestServer/     Fake MCP server, spoken to over a real stdio pipe
    ├── Humcp.Schema.Tests/   Golden tests over a real-world schema corpus
    ├── Humcp.Core.Tests/     The catalogue and the round trip, against the fixture
    ├── Humcp.App.UiTests/    FlaUI UIA-tree assertions (§9)
    └── fixtures/
        ├── schemas/          The corpus, harvested. Plus a manifest naming what refused
        └── golden/           One readable transcript per server, plus coverage.txt
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

### 6.2 Layout decisions taken in Phase 2

- **`tools/` is a third top-level directory**, for things that are neither shipped nor tests.
  `Humcp.Harvester` is in the solution so it builds with everything else and cannot rot, and it
  talks to the MCP SDK directly rather than through `Humcp.Core` — the corpus wants schemas exactly
  as servers emit them, and `Humcp.Core` sanitises everything it touches.
- **`ToolInfo` carries a `FormModel`, not a `JsonElement`.** The form is built once, when the tool
  list arrives. That detaches the schema from the document the SDK owns — so a `ToolInfo` outlives
  the listing it came from — and it means a hostile schema is defused at the edge rather than on
  whatever thread later opens a window.
- **`Humcp.Schema` has one package reference**, `JsonSchema.Net`, and `Humcp.Speech` and
  `Humcp.App` acquire it transitively. The rule that matters is unchanged: neither the MCP SDK nor
  WPF appears below `Humcp.Core`, and `Humcp.App` still references no protocol type at all.

### 6.3 Layout decisions taken in Phase 3

- **The editable form state lives in `Humcp.Schema`**, not in `Humcp.App` — a deliberate departure
  from the line above that puts view-models in the App, and the reason is what is in them.
  `FieldState` and `FormState` hold rules the schema implies: what an empty box means, when a
  default is worth repeating back, how typed text becomes the JSON type asked for, which field the
  caret goes to after a failed submit. None of that is a pixel, and rules that can only be exercised
  by launching a desktop application get exercised once. In `Humcp.Schema` they run against all 213
  harvested schemas on every build, which is how phase3-notes 3.2 was found. WPF binds to these
  objects directly; the App keeps everything that knows what a control is.
- **`FieldKind` → control lives in exactly one file**, `FieldTemplateSelector.cs`. That is what
  `FieldKind` being named for the shape of the data rather than for a WPF type buys (§6.1, hedge 2).
- **Automation ids are unique across the process, not across a window** — see §9, where the same
  fact is a testing rule.
- **`HUMCP_HISTORY` overrides where MRU history is written**, so the UI suite can run real tools from
  real forms without writing into the profile of whoever ran it.

### 6.4 Layout decisions taken in Phase 4

`ResultModel.cs` and `ResultBuilder.cs` are in `Humcp.Schema`, beside `FieldModel` and
`FormBuilder`, for the reason §6.3 gives about `FieldState`: what a result *says* is a set of rules
— that this is a table of eight rows, that this string is four thousand characters and wants a
window, that this structured content is the text again — and rules that can only be exercised by
launching a desktop application get exercised once.

What stayed in `Humcp.App` is what genuinely needs WPF: a decoded picture, a command a button can be
bound to, and the five `DataTemplate`s. `ResultSections` is the App-side view-model, and it is
deliberately thin.

Two rules went into `Humcp.Core` rather than into the App even though both are about the shell —
`MediaTypes` (what extension a saved file gets) and `LinkPolicy` (which links are handed to the
shell). They are security decisions with a right answer, and a security decision that can only be
tested by opening a window is a security decision that will not be tested.

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
5. **The result renderer takes `(schema, value)`, not `CallToolResult`.** Discharged in Phase 4,
   and it cost nothing: `ResultBuilder.Build(JsonElement? schema, JsonElement value)` is the
   signature, the schema is optional because most tools publish none, and `Humcp.App` still has no
   reference to the MCP SDK.

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

### Added by Phase 3

Three more, from [`phase3-notes.md`](phase3-notes.md). The first is about low vision rather than no
vision, and is the one most likely to be lost to a designer with good intentions.

20. **Nothing is signalled by colour alone.** A validation problem is set on the field as
    `ItemStatus`, written in words underneath the control, listed in the error summary, and marked
    visually with weight rather than hue. Half of this application's audience has some sight and is
    using it; a red border tells them there is a problem but not what, and tells a screen reader
    nothing at all. The same rule rules out red-means-required and grey-means-optional.
21. **An access key is unique or it is absent.** Windows cycles focus between controls sharing one,
    so on a generated form of twenty-three fields a duplicated key means <kbd>Alt</kbd>+<kbd>S</kbd>
    visiting four unrelated boxes in an order nobody can predict — worse than no shortcut. Assign
    over the whole form at once, prefer word-initial letters, and give up rather than duplicate.
    Assign over hidden fields too, so a key does not move when the optional toggle does.
22. **The caret goes to the first invalid field *in tab order*** — a refinement of §7.8.
    `ValidationReport` sorts shallowest-first and then alphabetically, which is right for reading a
    list of problems out loud and wrong for deciding where to put the caret: with `owner`, `repo`
    and `count` all missing it nominates Count, because C comes before O. Two orders, two jobs
    (phase3-notes 3.3).
23. **Anything bound into an items control needs a `ToString` worth hearing, or an explicit item
    name.** This is §7.12 again with a combo box instead of an `ItemsControl`, and it was found the
    same way — by ear, in all three screen readers at once. An item's accessible name is the plain
    text of its content, so a drop-down of `ChoiceOption` records announced
    *"ChoiceOption { Label = Titles, Value = …"* on every option, while looking perfectly correct on
    screen because `DisplayMemberPath` was set. `DisplayMemberPath` is a display concern and says
    nothing to UI Automation. Fix it in the object *and* in the item container style
    (phase3-notes 4.1).

24. **A message the reader does not speak has not been delivered.** §7.5 asks for a validation
    message on the field and in a summary, and `AutomationProperties.ItemStatus` is the obvious
    place for the first — but **NVDA does not speak `ItemStatus` on focus**, so the field's copy was
    set, correct and silent for a whole phase while a test asserted it was there. The message goes
    into the spoken help text as well, and first, because it is the news (phase3-notes 4.9). The
    general rule is the one that costs time to relearn: *a property being set is not the same fact
    as a property being announced*, and only listening tells them apart.
25. **The caret must survive whatever the keyboard does to the form.** Rebuilding the field list to
    reveal the optional parameters destroys the control the user is standing in, and WPF drops focus
    to the window — where a screen reader says the title and stops, with no clue what happened
    (phase3-notes 4.4). Anything that rebuilds part of a window puts the caret back. The same rule
    covers a target that has no control of its own: focusing a field that turned out to be a group
    means focusing the group's first member, never giving up quietly (4.5).
26. **Everything the platform gives a control of that shape, this one has too.** Typing a letter to
    jump to the next matching item is part of what Windows assumes of any list-shaped control, and
    WPF's `TreeView` is the one that does not implement it — so the application's primary navigation
    silently lacked it (phase3-notes 4.10). A generated UI is judged against the platform's
    expectations, not against its toolkit's defaults.

### Added by Phase 4

Four more, from [`phase4-notes.md`](phase4-notes.md). Two of them are about the way back from a
call; the other two were true through the whole of Phase 3 and were heard for the first time here,
because Phase 3 listened to fields and Phase 4 listened to buttons.

27. **A button bound to a `RoutedUICommand` announces that command's `Text` as its keyboard
    shortcut.** WPF's `ButtonBaseAutomationPeer` falls back to it for `AcceleratorKey`, so a command
    called "Save as" makes every button bound to it claim a shortcut it does not have — in the slot
    that correctly reads "U" for a button with a real access key. The form's Run button spent a
    phase announcing "Run" where it should have said "Control plus Enter". Commands carry no `Text`;
    a button with a real keystroke sets `AutomationProperties.AcceleratorKey` itself, spelled out
    (§7.15).
28. **Every part of a result has exactly one focusable control that says what it is.** A heading in a
    `TextBlock` and an explanation under it are on screen and in nobody's ear, and the buttons beside
    them are named for the action rather than for the thing — so an image announced itself only as
    "Save Image, PNG, 70 bytes". This is §7.24 generalised: it is not only validation messages that
    a window can know and never say.
29. **A control that navigates with the arrow keys sets its own current position when it takes
    focus.** A list box selects its first item; a grid sets a current cell. Without it the control
    does not handle the first arrow key at all, and WPF treats the press as directional navigation
    and moves focus out of the control — a grid of two hundred rows, all of them on screen and none
    of them reachable, with nothing announced. Found twice in two phases and two controls
    (phase3-notes 4.8, phase4-notes 4.3).
30. **A `Focus()` return value is not a report about where the caret went.** `TreeView.Focus()`
    answers `false` while moving focus into the tree, because focus belongs to the selected item —
    so a pane cycle that trusted the answer skipped the tree entirely and <kbd>F6</kbd> stopped
    reaching the application's primary navigation. Ask the control where the focus is.

Also added to the keyboard map below: <kbd>Escape</kbd> and <kbd>Ctrl</kbd>+<kbd>W</kbd> close a
window. **`Button.IsCancel` is not how** — it sets `DialogResult` on a window shown with
`ShowDialog` and does nothing at all on one shown with `Show`, which is every window humcp opens
(phase3-notes 4.2). And a window-level shortcut that a focused control also handles has to be taken
in a preview: a WPF `Button` swallows <kbd>Ctrl</kbd>+<kbd>Enter</kbd> as a plain Enter and presses
itself (4.7).

**Three smaller ones from the same pass, each a rule about a specific control:**

- **A reserved-letter list goes stale.** Access keys are assigned around the letters the window's own
  chrome uses (§7.21), and the menu bar was added without extending that list — so
  <kbd>Alt</kbd>+<kbd>T</kbd> opened the Tool menu instead of the Tags field, which a menu mnemonic
  always wins (phase3-notes 4.6).
- **A list selects what it focuses**, or every command that acts on "the selected entry" is dead on
  arrival — including the <kbd>Delete</kbd> its own help text promises (4.8).
- **A menu item's check mark is not a state** unless `IsCheckable` is set: WPF draws the tick and
  exposes no toggle state, so the window you are in is the one nobody is told about (4.2).

### Keyboard map

| Key | Action |
|---|---|
| <kbd>Alt</kbd> | Menu bar |
| <kbd>F6</kbd> / <kbd>Shift+F6</kbd> | Cycle panes within a window |
| <kbd>Ctrl+1</kbd>…<kbd>9</kbd> | Switch to nth open window (built in Phase 3, §8) |
| <kbd>Ctrl+W</kbd> | Close the current window |
| <kbd>Ctrl+F</kbd> | Focus tree filter box |
| <kbd>Enter</kbd> (on a tool) | Open its form window |
| <kbd>F1</kbd> | Full details for the focused tool or field |
| <kbd>Ctrl+Enter</kbd> | Submit form |
| <kbd>Enter</kbd> (in a result) | Open what the caret is on in a window of its own |
| <kbd>Ctrl+Shift+C</kbd> | Copy the value the caret is on |
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

### The window model

Windows are **non-modal** — several tool forms and results can be open at once. Built in Phase 3
after the first manual pass reported that the form windows "read like dialogs rather than windows"
(phase3-notes 4.3), and modelled on the multi-document applications that solved this before, mIRC
among them:

- **Every window has its own menu bar**, carrying a `Window` menu that lists every window humcp has
  open, numbered, with the current one checked. Reached with <kbd>Alt</kbd>, never in the tab order
  (§7.14).
- **<kbd>Ctrl</kbd>+<kbd>1</kbd>…<kbd>9</kbd> switches** to the nth window from anywhere;
  <kbd>Escape</kbd> or <kbd>Ctrl</kbd>+<kbd>W</kbd> closes the current one.
- **Only the main window appears in <kbd>Alt</kbd>+<kbd>Tab</kbd>** (`ShowInTaskbar = false` on
  everything else). A session across several servers can have a great many windows open, and putting
  all of them in the system switcher buries every other application on the machine. Inside humcp the
  same list is numbered, named and checked, which is more use to a screen reader than a strip of
  icons.

Every window humcp opens is tracked this way, including the sub-form window for a row of an object
list and the <kbd>F1</kbd> details and diagnostics windows.

Menu bar contents: File (add/remove/reconnect server, settings, exit), Edit (standard clipboard
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

**Built in Phase 2**, and four things about how it is built are load-bearing:

- **It is harvested by a tool, not curated by hand.** `tools/Humcp.Harvester` starts each server in
  its checked-in `servers.json` over a real stdio pipe and writes down what `tools/list` answers.
  The corpus's whole value is that nobody here chose what is in it; tier coverage measured against
  schemas we invented would measure our imagination.
- **Credentials in the harvest list are deliberately fake.** `tools/list` is metadata, and nearly
  every server publishes its catalogue before authenticating anything.
- **`manifest.json` records the servers that refused, and why.** A corpus listing only its successes
  is not evidence — and the single most useful finding of Phase 2 came out of a failure line
  (phase2-notes 3.1).
- **Golden files are readable transcripts, one per server, not serialised object graphs.** A diff
  has to be judgeable by eye. `HUMCP_UPDATE_GOLDEN=1` regenerates them and still fails the test, so
  a regeneration can never pass unreviewed. `coverage.txt` is regenerated alongside them, so the
  headline metric moving is a line in a diff rather than something someone has to remember to check.

**Result: 213 tools from 22 servers, 785 top-level parameters. 91.2% render at Tier 1, 95.0% at
Tier 1 or 2, and all 213 schemas can be validated against.**

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

Phase 3 added three more, all three learned by chasing a failure that was not where it looked:

- **An owned window is a *child* of its owner in the automation tree.** A generated form opens
  non-modally over the main window, and UI Automation reports it underneath it — so
  `FindFirstDescendant` from the main window reaches into every form that is open. **Automation ids
  therefore have to be unique across the process, not across a window.** The form's Run button and
  the main window's Run button were both `RunButton`; a test meaning to press one pressed the other,
  ran the wrong tool with no arguments, and reported a server error three tests later.
- **`GetAllTopLevelWindows` does not return owned windows**, though each has its own top-level
  handle. Look for a form among the owner's descendants, or conclude it never opened while it is
  open and announcing itself.
- **A label and its control share a name by design** (§7.1 and §7.3 both), and the label comes first
  in the tree. A lookup by name has to filter to keyboard-focusable elements or it gets the label,
  which has no `ItemStatus` and no value pattern, and then asserts something false about it.
- **A window's title bar carries a `MenuBar` of its own** — the system menu — so "find the menu bar"
  finds that one. Name your own and look it up by name. And **a WPF `Menu` reports as
  `ControlType.Menu`, not `MenuBar`**; only the system menu is a `MenuBar`.
- **Setting a text box's value through the value pattern is not typing.** It does not raise
  `CommandManager.RequerySuggested`, so a button whose availability follows that text stays disabled
  for ever — which reads exactly like a defect in the application. Where the thing under test is a
  command's availability, send real keystrokes.

Phase 4 added four more, three of which are about scoping a lookup or a claim rather than about
UI Automation itself:

- **A cycle asserted at one end is not asserted.** The <kbd>F6</kbd> test pressed the key twice and
  looked for the result pane; it passed for a whole phase while the tree had silently dropped out of
  the cycle (phase4-notes 4.1). Walk the whole ring.
- **A `DataGrid` refuses `SetFocus` through UI Automation**, exactly as a `TreeView` does. Open the
  window that focuses it, or tab into it.
- **A bare `ContentControl` creates no automation peer**, so a view built on one is absent from the
  tree and nothing can be scoped to it. Give it a peer reporting an unnamed `Group`.
- **The result view in the main window and the one in a form need different automation ids**
  (`MainResults`, `FormResults`). Phase 3 learned this about two buttons called `RunButton`; it is
  just as true of two panes, and for the same reason — an owned window is a descendant of its owner.

Three more rules about writing these tests, from the same phase. **Wait for a state, never for a
duration**: for the automation tree to stop changing after fields are revealed, for a window to
have closed, for a button to have become enabled after a selection. All three were intermittent
failures here, and all three fail only when somebody else is using the machine — which is the worst
possible time to be told your test suite is unreliable. **Drive setup through UI Automation
patterns rather than keystrokes** — `Invoke`, `Select`, `Toggle` need no foreground, and getting a
form open is setup for almost every assertion, so doing it by typing takes the whole file down
whenever somebody is using the machine. Reserve keystrokes for the claims that are *about* the
keyboard.

The suite runs as part of `dotnet test` rather than behind a flag. It takes the foreground and
sends real keystrokes, which is the point of it. It also runs real tools from real forms, so MRU
history goes to a temporary directory via `HUMCP_HISTORY` — a test run must never leave anything in
the profile of whoever ran it.

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

### Phase 2 — Schema engine ✅ done
`Humcp.Schema` in full: all three tiers, `$ref` resolution with cycle detection, validation,
description sanitisation. Harvest the corpus and build golden tests. No UI work.

**Exit met, with room.** 95.0% of the corpus's 785 top-level parameters render at Tier 1 or 2 — and
91.2% at Tier 1 alone, which is the stricter §9 metric. Every one of the 213 harvested schemas
produces a form, and there is no input for which `FormBuilder.Build` throws. 813 automated tests.

The corpus is the phase's real product: 22 published servers, none of whom have heard of this
project, and no per-server code anywhere in the repository. Three measurements settled arguments
that would otherwise have been settled by taste — the discriminated-union promotion recovers nothing
(§13), `format: date` never appears (§13), and treating `anyOf: [X, null]` as a union would have
sent every optional parameter of every C# MCP server to a JSON editor (§5). Full record in
[`phase2-notes.md`](phase2-notes.md).

Outstanding, neither blocking: the JAWS and Narrator passes, still carried forward from Phase 0.

### Phase 3 — Form generation ✅ done
`DataTemplateSelector` over `FieldModel`. The full accessibility contract (§7). Required/optional
toggle, the enum-first wizard heuristic, MRU history for parameter values, validation surfacing.

**Exit:** FlaUI suite green; a tool with 15 parameters is comfortable to fill in by ear.

**Exit met.** 902 automated tests green, 36 of them walking the live UIA tree, and the manual pass
walked end to end under NVDA 2026.1. Eleven templates cover the whole control vocabulary, and no
line of the application knows anything about any particular tool.

**The manual pass found nine defects, and the automated suite had found none of them** — that is the
number worth remembering from this phase. A combo box announcing `ChoiceOption { Label = …` on every
option. A Close button that closed nothing, and <kbd>Escape</kbd> likewise. Windows that read like
dialogs. The optional-fields shortcut throwing the caret out of the form. A form that opens on an
object leaving it nowhere. <kbd>Alt</kbd>+<kbd>T</kbd> opening a menu instead of a field.
<kbd>Ctrl</kbd>+<kbd>Enter</kbd> pressing whatever button the caret was on.
<kbd>Delete</kbd> doing nothing in a list that says <kbd>Delete</kbd> removes things. And a
validation message the reader never spoke. All nine are fixed, each has a test, and each fix was
re-checked by ear. Full record in [`phase3-notes.md`](phase3-notes.md) §4.

Three parts of the brief came back changed. The enum-first wizard heuristic is now a focus rule and
not a reordering one (§4, pitfall 4) and the form gained a rule nobody asked for about not sending
things (§5) — both after asking the corpus rather than arguing. The window model of §8 moved forward
from Phase 6, because it is what the user meets first. Seven additions to the contract (§7.20–26),
six to the testing notes (§9), one layout decision (§6.3), and §13's tool-grouping question
answered: the prefix groups help and they stay.

Outstanding, neither blocking: JAWS and Narrator have not seen the nine fixes, and item 25 of the
checklist — the application with no screen reader running at all — wants a sighted pass.

### Phase 4 — Result rendering ✅ done
`structuredContent` + `outputSchema` → `DataGrid` or property tree. Multi-part `content` arrays.
Images as objects with save/open. `resource_link` and embedded resources. `isError` handling.

**Exit:** every content type has a defined, non-degenerate accessible presentation.

**Exit met.** Every kind of part has a control of its own and a name on it: text in a caret-navigable
box, a picture with Save and Open beside a sentence saying what it is, a link with an action only
where humcp will follow one, an embedded resource as text or as a file, a table as a real grid with
column headers, and anything else structured as a tree read "label, value". 1,013 tests, 51 of them
over the live UIA tree.

**The corpus decided the shape of the component**: only **25 of its 213 tools publish an
`outputSchema` at all**, and 17 of those 25 are one property wrapping the answer — 13 of them a bare
string the tool has already sent as text. So the renderer reads the shape from the *value* and takes
names from the schema where there is one, takes the wrapper off, and does not draw a structured
result that merely repeats the text (phase4-notes 3.1–3.3). Written schema-first it would have had
nothing to show for 188 of 213 tools.

**The manual pass found eight defects and the automated suite had found none of them** — the same
sentence as Phase 3 — and re-checking the fixes by ear turned up a ninth. <kbd>F6</kbd> had stopped
reaching the tree; a grid announced
`ResultRow { Number = 1, Cells = System.Collections…` on every row; an arrow key in a grid landed in
the menu bar with two hundred rows unreachable; a picture said what it was to nobody; every
command-bound button in the application announced the command's name where a screen reader says the
shortcut; a link read "speaks.. text/html"; a nested value opened as JSON; and the completion
announcement mixed three registers in one sentence. All fixed, each with a test. Four additions to
the contract (§7.27–30), four to the testing notes (§9). Full record in
[`phase4-notes.md`](phase4-notes.md).

Two new rules that are security decisions rather than accessibility ones, both in `Humcp.Core` so
they are testable without a desktop: humcp chooses a saved file's extension from an allow-list of
media types and never from the server, and it hands only `http`/`https` to the browser, revealing a
`file:` URI in a folder rather than opening it (phase4-notes 3.5).

Outstanding, neither blocking: JAWS and Narrator, still carried from Phase 0, and now a picture on
screen that no sighted pass has looked at.

### Phase 5 — MRTR and elicitation (~3–4 days)
Declare the `elicitation` capability. Handle `resultType: "input_required"`: build the form from
`requestedSchema`, honour accept/decline/cancel, echo `requestState` verbatim, retry with a new
JSON-RPC id. Decline sampling requests cleanly (deprecated, and no LLM in v1).

**Exit:** a multi-step server tool completes through the GUI.

### Phase 6 — The shell (~1 week)
Settings, server add/remove/reconnect UI, config persistence,
`notifications/tools/list_changed` handling via `subscriptions/listen`, announcement policy with
rate limiting.

The window manager and per-window menu bars were built in Phase 3 instead (§8), so what is left here
is the menus' *contents* — File, Edit, View and Help — and the settings behind them.

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
- **A grid that can be sorted, and says that it has been.** A `DataGrid` sorts by a column header
  for nothing, and a sort a screen reader user cannot tell has happened is worse than no sort
  (phase4-notes 6). The same goes for the <kbd>Applications</kbd>-key context menu the keyboard map
  promises.
- **A way to turn MRU history off and empty it.** It is a plain-text file of things people typed,
  and although a parameter whose name reads like a credential is never recorded (phase3-notes 3.10),
  that is a heuristic and the user should have the final say. `MruHistory.Forget()` already exists.

**Exit:** the app is configurable and usable without editing files by hand.

### Phase 7 — Hardening and release (~1 week)
Streamable HTTP transport (unauthenticated). Robust error and disconnect handling. Cancellation.
Untrusted-input hardening. Packaging and a signed installer. User documentation written for
screen reader users.

One item carried in from Phase 2:

- **One malformed tool schema must not cost the user every other tool on that server.**
  `ModelContextProtocol.Core` 2.2.0 requires each `inputSchema` to be an object schema and throws
  while deserialising the whole `tools/list` response when one is not — which is how nine perfectly
  usable GitLab tools became a failed server (phase2-notes 3.1). Either list through a lower-level
  call and map tools ourselves, or get the SDK to report per-tool. The schema engine cannot help:
  the listing never reaches it.

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
- ~~Tool grouping heuristics: prefix-splitting is a guess.~~ **Answered at the Phase 3 manual pass:
  the prefix groups help, and they stay as they are** (phase3-notes 4.5). The thresholds are
  unchanged and remain three constants in one method, so a forty-tool server can reopen the question
  cheaply.
  <br>What it was asking: Phase 1's version (§8) reads well on a ten-tool server, which proves
  nothing about a forty-tool one, and Phase 2 deliberately did not ask the corpus because the
  question is about how a tree *sounds* rather than what is in it. It wanted a screen reader, and
  now it has had one. Worth re-asking against a server past twenty tools.
- ~~Does `discriminator`-style promotion of `oneOf` to Tier 2 pay for MCP servers?~~ **Answered: no,
  for now.** Of the 18 top-level parameters that reached Tier 3 through a union, zero are a union of
  object branches pinned by a constant discriminator; almost all are `anyOf: [object, string]`,
  where rendering the object branch would hide the string shorthand (phase2-notes 3.3). §12's
  argument that the benefit flows backwards from OpenAPI is not wrong, but it is currently worth
  nothing on MCP, so §5's placement of `oneOf` at Tier 3 stands. Re-measure at v4 planning; the
  harvester makes that a re-run rather than a study. The same pass killed the typed-map dictionary
  editor: 11 of 12 free-form-object parameters accept any key at all.
- **Where should Phase 3's effort go?** The corpus says `format: date` appears in *none* of the 785
  real parameters — the formats that do are `uri`, `uuid`, `int32` and `json` (phase2-notes 3.4).
  This reverses nothing: elicitation blesses `date` explicitly (§3), so Phase 5 needs a `DatePicker`
  and Phase 0 already accepted one. It does mean the `DatePicker` and its §7.15 keystroke hint sit
  on a path no server in the corpus takes, while the plain text box sits on a path 91% of parameters
  take. Spend the polish accordingly.
- **Does prism have a job at all?** Phase 1 answered the narrow question — a live region reaches
  NVDA unaided, and speaking on top of it announces everything twice (§7) — which leaves the seam
  loaded and idle. The remaining case is an event arriving while humcp is in the background, where
  no live region will be read: a slow call finishing, a connection dropping. Whether speaking from
  a window the user is not looking at is welcome or intrusive is a judgement, not a capability, and
  it wants a session with a screen reader rather than an argument (Phase 6).
- **Does a list of primitives want an Edit as well as Add and Remove?** Phase 3 built Add and Remove
  only, because editing through the entry box is a mode and a mode nobody can see the state of is
  the thing this application exists to avoid (phase3-notes 3.8). If the manual pass finds re-adding
  a mistyped entry annoying, the version to build is an Edit that opens a window — a window has a
  name, and names are announced.
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
