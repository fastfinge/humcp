# Phase 13 — Resources

v2's sixth phase (ROADMAP §11, "Built in this order"), 2026-10-23. A server's resources are in the
tree beside its tools and prompts and are read from it; a template is a form for the parts of its
address, with the server's suggestions behind them; a link to a resource in any result is read back
through the server that sent it; and a tool's parameter that wants an address offers the server's
resources, which closes the second half of §4's pitfall 3.

**Status: complete.** Schema 903, Core 289, UI 98 — all green; the NVDA pass found four defects that
the suite had not (§4), each fixed and re-checked by ear.

## 1. Decisions

- **Resources are two more operation kinds**, as prompts were one (phase12-notes §1):
  `OperationKind.Resource`, a fixed address with an empty form, read straight from the tree the way a
  tool with no parameters is run; and `OperationKind.ResourceTemplate`, whose RFC 6570 variables are
  its form. `ToolInfo` carries the address (`ResourceUri`) and the media type. The words that differ
  by kind — *Read resource*, *Reading*, *arguments*, the *Resource* menu — now come from `ToolInfo`
  (`RunVerb`, `Doing`, `ArgumentWord`, `Noun`) rather than from `IsPrompt` tests scattered through
  the App.
- **humcp parses a template's variable names and the SDK does the expansion.** `ResourceForm.Variables`
  reads names out of every expression, ignoring prefix and explode modifiers; the SDK's
  `ReadResourceAsync(template, arguments)` makes the address. A variable in a query expression
  (`{?…}`, `{&…}`) is optional, because the address still exists without it; every other is required.
- **A resource's name is shown as written, not humanised.** It is usually a file name or an address;
  `NameHumanizer` made "readme.md" into "Readme md". Title, then name, then address.
- **The tree's Resources category lists fixed addresses by name, then templates, and is never
  grouped**: the prefix heuristic would put every resource of a file server under "File".
- **A `resource_link` in a result gains Read resource**, for every scheme except `http`/`https` (the
  browser's). Reading goes to the server, never to the shell, so `LinkPolicy` is untouched: a
  `file:` link can be read *and* shown in its folder; `ms-settings:` can be read, which asks the
  server and nothing else. The resource opens in a window of its own, owned by the window the link
  was in, so the result being read stays put and closing the new window returns to it.
- **A tool parameter wants an address** when its schema says `format: uri` (or `uri-reference`,
  `iri`, `iri-reference`), or it is named `uri`, `…Uri`, `…_uri` or `…-uri`. Not `url`, which is
  nearly always a web address a resource list would only clutter. Such a field offers the server's
  fixed resource addresses after this user's history, in a drop-down whose help says so, fixed when
  the form opens (up to 100).
- **No subscriptions to a single resource's changes.** `resources/list_changed` is re-listed with the
  tools and prompts; `notifications/resources/updated` would need a reason to watch one resource, and
  nothing here gives one yet.

## 2. What was built

- **Core:** `OperationKind.Resource` and `ResourceTemplate`; `ResourceForm` (variables, the two
  builders, `WantsResourceUri`), tested without a server; `ServerConnection` lists resources and
  templates separately (either failing costs only itself), reads them, completes a template's
  variables through `ref/resource`, re-lists on `resources/list_changed` (through
  `subscriptions/listen` on 2026-07-28), and turns a linked address into an operation
  (`LinkedResource`), using the listed resource where there is one; `ResourceTextResultBlock.IsEmbedded`,
  so a resource that was read is headed "Resource", not "Embedded resource"; a blob whose type is a
  picture or a sound is one, with its preview and its alt text.
- **Schema:** `TextFieldState.OffersResources`; `AccessKeys.ResourceCommands` (S, W, R, C, O).
- **App:** the *Resources* field template; Read resource on links (`ResultCommands.Read`); the main
  window's and every form window's link reader; `ToolWindow.RunWhenShown` for a window that reads as
  it opens; resource nodes in the context menu (Copy address) and in F1.
- **Fixture:** three resources (text, a picture, JSON) and a template whose variable it completes;
  one new tool, `result_link_to`, whose `resource_uri` parameter offers them and whose answer links
  to one. The fixture now publishes 32 tools.

## 3. Against a real server

`@modelcontextprotocol/server-everything` publishes seven documents and three templates. Its
*architecture.md* reads as 45 lines; *Dynamic Text Resource* with 3 reads "Resource 3: This is a
plaintext resource…"; its *Get Resource Links Tool* answers with three links, and Read resource on
"Text resource 2" opens a window of that name holding it.

## 4. The NVDA pass

Four defects, each fixed and re-checked by ear:

1. **Reading a resource was announced "Tool call complete. An embedded resource, 45 lines."** Wrong
   on both counts. `ResultFormatter.Announcement` now takes the operation: "Resource read. 45 lines
   of text.", and a failure is "The resource could not be read."
2. **A template's variable said nothing about itself.** The protocol gives variables no descriptions,
   so the field announced only how to open its drop-down — while the one thing the user needed,
   that server-everything's `resourceId` "must be an integer", was in the template's description and
   heard only on the tree item. A template's only variable now carries the template's first
   sentence; every variable says which address it is part of (in F1).
3. **A resource read from a link it was never listed under titled its window with its address**,
   "demo://resource/dynamic/text/2", when the link had called it "Text resource 2". The link's label
   now names it.
4. **Closing that window put the caret in the main window**, not back on the button in the window
   whose link was followed — the reader's place lost. The new window is owned by the one it came from.
   (WPF closes owned windows with their owner, so closing a result also closes what was read from it.)

Heard, and fine: "Connected to 2 servers, 54 tools, 8 prompts, 14 resources."; "architecture.md,
text/markdown" on arrow-through; the template's "template, 1 required argument"; Phase 12's "no
suggestions yet" on a template whose server has none until something is typed; each link's
explanation, "Read resource asks the server for it, in a window of its own. humcp will not hand this
address to Windows."

Noticed and not this phase's: NVDA's title command answered "humcp" from inside an owned window,
where it might be expected to say the window's own title. Not investigated.

## 5. Not done here

- **No watching a resource** (`resources/subscribe` is gone in 2026-07-28; its replacement is a
  `subscriptions/listen` filter per URI). A user re-reads instead.
- **A file server's thousands of resources are one flat list**, capped at 2,000 like tools. A tree
  shaped by the addresses' paths would serve a filesystem better; nothing measured says how often
  that matters.
- **Resources are not in search.** As with prompts (phase12-notes §1).
- **The drop-down of resources on a tool's parameter is fixed when the form opens**, so a resource
  that appears afterwards is not offered until the form is opened again.
