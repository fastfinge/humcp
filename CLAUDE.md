# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project status: Phases 0, 1 and 2 complete, Phase 3 next

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

Still outstanding from Phase 0 and non-blocking: the JAWS and Narrator passes.

The next unit of work is **Phase 3** (§10): `DataTemplateSelector` over `FieldModel`, the full
accessibility contract (§7), the required/optional toggle, the enum-first wizard heuristic, MRU
history, and validation surfacing. `FieldKind` is named for the shape of the data rather than for a
WPF type precisely so Phase 3 owns that mapping.

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
| Server-initiated `elicitation/create` / `sampling/createMessage` / `roots/list` | Removed. Replaced by **MRTR**: `tools/call` returns `resultType: "input_required"` with `inputRequests` + opaque `requestState`; client retries the original call with `inputResponses` and a **new** JSON-RPC id |
| `ping`, `logging/setLevel`, `resources/subscribe` | Removed. Subscriptions go through `subscriptions/listen` |
| Roots, Sampling, Logging as things to build on | **Deprecated** |
| Results without `resultType` | All results carry `resultType`; treat a missing one from older servers as `"complete"` |
| `inputSchema` restricted to simple shapes | Loosened to any JSON Schema 2020-12 keyword, including `$ref`, `oneOf`, `if`/`then`/`else` |

Also note: `completion/complete` supports only `ref/prompt` and `ref/resource`. **There is no
`ref/tool`** — tool arguments cannot be autocompleted by the server, which is why local MRU history
exists (§4, pitfall 3).

Never hand-roll the protocol. `ModelContextProtocol.Core` 2.2.0 implements this revision and
negotiates back to older servers — confirmed in Phase 1: 2026-07-28 against our fixture,
2025-11-25 against `@modelcontextprotocol/server-everything`, same code either way.

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
src/Humcp.Schema/   net10.0          JSON Schema -> FieldModel. Pure. No MCP SDK, no WPF.
src/Humcp.Core/     net10.0          Server registry, connections, tool catalogue, MRTR, MRU. No UI.
src/Humcp.Speech/   net10.0-windows  IAnnouncer over prismatoid, plus a no-op implementation.
src/Humcp.App/      net10.0-windows  WPF: views, templates, view-models, window manager.
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

`Humcp.Schema` is the core IP and is pure logic — build and test it without a UI.

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
add a test that types without calling `Input.EnsureForeground` first.

Phase 1's server is hard-coded to the in-repo fixture. To point the app at a real server instead,
without a code edit:

```bash
set HUMCP_STDIO_COMMAND=npx.cmd
set HUMCP_STDIO_ARGS=-y @modelcontextprotocol/server-everything
dotnet run --project src/Humcp.App
```

## Testing notes

- **The schema corpus is the highest-value test asset** (§9): 213 real `inputSchema` documents from
  22 published MCP servers in `tests/fixtures/schemas/`, with golden tests over the resulting
  `FieldModel`. Its whole value is that nobody here chose what is in it — never hand-write a fixture
  into it. Regenerate with `dotnet run --project tools/Humcp.Harvester` (optionally naming server
  ids); `manifest.json` records the servers that refused and why, on purpose.
- **Golden files are readable transcripts, one per server.** Regenerate with
  `HUMCP_UPDATE_GOLDEN=1 dotnet test tests/Humcp.Schema.Tests`, which rewrites them **and still
  fails**, so a regeneration cannot pass unreviewed. Read the diff. `coverage.txt` carries the
  headline metric and is regenerated with them.
- The fake in-process MCP server should exercise hostile input deliberately — 10,000-character
  descriptions, control characters, deeply nested schemas — since all of it is untrusted.
- Automated UIA assertions prove names exist; only manual NVDA/JAWS/Narrator testing proves the app
  is pleasant. Do both at each phase exit.
