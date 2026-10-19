# Phase 7 — Hardening and release

**Goal:** Streamable HTTP (unauthenticated), robust error and disconnect handling, cancellation,
untrusted-input hardening, packaging and an installer, user documentation for screen reader users —
and the two items carried in: a destructive tool confirms, and one malformed schema does not cost a
server its other tools (ROADMAP §10).

**Exit:** v1.0.

**Status: built, suite green, manual pass walked, released as 1.0.0.** Schema 869, Core 186, UI 79.
The NVDA pass, driven through the screen reader bridge under the *user* stance as Phase 6's was,
found three defects the suite had not (§3); all are fixed and re-heard.

---

## 1. What exists

```
src/Humcp.Core/
├─ ServerConnection.cs      Transport(): stdio or Streamable HTTP; Watch(): a session that ends on
│                           its own; ListToolsAsync(): the SDK's listing, then a raw per-tool one;
│                           MaxTools; the sign-in sentence for 401/403
├─ ServerForm.cs            Address, beside Command: one or the other
└─ ServerRegistry.cs        ServerAnnouncements.Stopped and .Destructive

src/Humcp.App/
├─ ChildProcessJob.cs       a job object: every server ends when humcp does, however it ends
├─ App.xaml.cs              the last-chance handler, and CrashLog (%LOCALAPPDATA%\humcp\errors.log)
├─ ToolWindow.xaml(.cs)     Stop, Escape-stops-a-call, the destructive confirmation
├─ MainWindow.xaml.cs       the same for the tree; a server stopping is announced; Help, User guide
└─ Humcp.App.csproj         ships LICENSE, notices, licence texts and the user guide

tests/Humcp.TestServer/     --http <port> (Streamable HTTP on loopback, via ModelContextProtocol.AspNetCore,
                            test-only), --exit-after <ms>, --malformed
tests/Humcp.Core.Tests/HardeningTests.cs

installer/                  the MSI: WiX Toolset 7 as an MSBuild SDK project, per user, no administrator rights
.github/workflows/          ci.yml (build, Schema, Core); release.yml (tag → tested, published release)
docs/user-guide.md          written for screen reader users; also Help, User guide
LICENSE (0BSD), THIRD-PARTY-NOTICES.md, licenses/, README.md, CHANGELOG.md, SECURITY.md
```

## 2. Findings

### 2.1 Closing humcp left its servers running

Found by accident: three fixture servers, from three UI test runs, still running twenty minutes
later. `Closed` started the registry's disposal and did not wait for it, so the process exited first;
and a crash or a kill could never have stopped them at all. humcp now puts itself in a job object with
`KILL_ON_JOB_CLOSE` before starting anything, so every child — and an `npx` server's `node`
grandchild — ends with it, whatever ends it. Connection diagnostics says whether the job could be
made.

### 2.2 The SDK refuses a malformed schema by refusing the whole listing

`Tool.InputSchema`'s setter throws for a non-object schema, so `ListToolsAsync` throws for the whole
server (phase2-notes 3.1). humcp catches that, asks for `tools/list` again as plain JSON, and reads
each tool itself; the broken tool becomes a Tier 3 form like any schema humcp cannot draw. The fixture
cannot publish such a tool honestly — the SDK will not build one — so `--malformed` rewrites a tool's
schema in an outgoing message filter.

### 2.3 Streamable HTTP is the SDK's, and sign-in is refused in words

`HttpClientTransport` with `AutoDetect`, and `OAuth` deliberately unset: a server that answers 401 or
403 gets *"This server wants you to sign in. humcp cannot do that yet: signing in arrives in version
3."* Only `http` and `https` addresses are accepted, checked before anything is sent. The add-server
form gained an Address field beside Command, because a transport you cannot configure without editing
a file is not configurable (§10, Phase 6's exit).

### 2.4 A session that ends on its own is a state, not silence

`McpClient.Completion` is watched; when it completes and humcp did not end it, the server is marked
failed with its exit code and last words, and the tree and the status line say so, with F5 as the way
back. The App announces it through the same quiet-waiting path as a list change (§7.34).

### 2.5 Escape stops a call before it closes a window

Closing a form already cancelled its call, and took the form and the result pane with it. While a call
runs, Escape now stops it and leaves the window; the second Escape closes it. The tree's own calls stop
the same way. A Stop button exists for anyone who wants one, enabled only while a call runs, and
hands the caret to Run when it disables itself (§7.25).

### 2.6 A destructive tool asks every time

§3's table promised it from the start and no phase built it. Warn once, run on the second press — the
same idiom a submit and a server removal use — and the sentence says whose claim it is: *"Delete
everything says it may delete or change things that cannot be put back."*

### 2.7 Progress arrives after the answer, under load

Found by the release workflow, not by a person: on GitHub's runners, about one run in three, a call
received none of its progress. Traced on both sides — the fixture logging every notification it sent,
the client every one it received — the five notifications arrived with the right token, *after* the
answer that ended the call, though they came first on the wire. The SDK's per-call progress handler is
gone by then, and so was humcp's first replacement for it. Progress is now received by a handler
registered once per connection and routed by a token humcp generates, kept for thirty seconds after
the call. Late reports reach the call; the App speaks none of them once the answer is in. Six
parallel CI runs green afterwards. Never once reproduced locally, even pinned to two cores.

### 2.8 Licensing

Nothing humcp depends on restricts the licence of humcp's own code: Apache-2.0 (the MCP SDK), MIT
(.NET, WPF, Humanizer, json-everything's source), MPL-2.0 (Prismatoid, used unmodified, file-scoped).
So humcp is 0BSD, the most permissive OSI-approved licence there is. The json-everything NuGet
binaries carry an *Open Source Maintenance Fee Agreement* on top of MIT; it allows redistribution
under MIT and does not bind humcp's licence, and `THIRD-PARTY-NOTICES.md` explains it so that an
organisation it might concern can decide for itself.

## 3. What the manual pass found

Four servers at once: the fixture, a copy that exits after 25 seconds, a copy publishing a malformed
schema, and the fixture over HTTP, added through the form's Address field.

### 3.1 humcp's own words were read out as the server's

*"Short-lived server stopped … Server said: The server would not keep a subscription open, so humcp
will not hear when its tools change: MCP server process exited …"* "Server said" quoted the last line
of humcp's diagnostics, not the server's standard error, and the listen request failing because the
session had ended had just written one. The server's own last line is now kept apart and is the only
thing ever quoted; a listen failure is not recorded once the session is over. A test pins both.

### 3.2 "Stopped" twice

*"Short-lived server stopped. The server stopped, with exit code 3."* The reason is now *"It exited
with code 3."*

### 3.3 The user guide split its sentences and read its syntax

Hard-wrapped at a hundred characters, every Down Arrow read half a sentence; and every heading began
"number sign". The guide is one line per paragraph now, with its troubleshooting entries and keyboard
groups as real headings, and the in-app window drops the heading marks.

### 3.4 What the pass confirmed

*"Connected to 3 servers, 91 tools"* — the malformed server kept all thirty of its ordinary tools and
showed the broken one. A server dying was announced unprompted with what to do about it. The first
Enter on a destructive tool warned in the server's name and the second ran it. Escape stopped a
running call — *"Stopped Slow tool. The server was told; anything it had already done stays done"* —
and nothing finished behind the user's back. An HTTP server added by address connected and was
announced like any other. Help, User guide opened in the read-only window.

### 3.5 Heard and left

The first Down Arrow from the guide's title repeats the line after it once; a WPF text box reporting
an empty line, and harmless.

## 4. Decisions taken

| Decision | Why |
|---|---|
| An MSI, per user, no administrator rights | Windows Installer is the native format, and what an IT department deploys; its dialogs are standard Win32 controls. Built with WiX 7 as an MSBuild SDK project, so it builds like the rest of the solution. Inno Setup was the first choice and was replaced at the developer's request. MSIX waits for signing, since Windows will not install an unsigned one. |
| WiX 7, OSMF EULA accepted by the developer | Recorded as `AcceptEula` in the project. The fee applies to revenue-generating use by organisations with US$10k or more a year; WiX 5.0.2 (MS-RL) is the drop-in fallback for anyone it concerns. Per-user installs under the profile suppress ICE38, ICE64 and ICE91, which are about roaming profiles; the install and uninstall are tested for a clean round trip instead. |
| Unsigned 1.0, with SHA-256 checksums | Signing needs a certificate this project does not yet have; SignPath's free open-source signing is the obvious next step. |
| UI suite local, not in CI | It sends real keystrokes and needs a desktop nobody else is using; CI runs build, Schema and Core, and the release workflow adds a start-up smoke test and an installer test. |
| The user guide is a text file and a window | Read in the same caret-navigable window as everything else in humcp; Notepad from the Start menu. |
| 1.x is fixes, 2.0 and 3.0 are §11 | The developer's versioning, recorded in `CHANGELOG.md`. |

## 5. What is outstanding

- Code signing: SignPath Foundation's free open-source programme, which needs a published release
  and a code-signing policy on the project page to apply; then MSIX through the Microsoft Store,
  whose free individual registration includes Store signing.
- v2 (§11).
