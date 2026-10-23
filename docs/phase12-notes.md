# Phase 12 — Prompts

v2's fifth phase (ROADMAP §11, "Built in this order"), 2026-10-23. A server's prompts are in the tree
beside its tools, open as the same generated form, fetch as a conversation whose messages say who
speaks them, and — the one place MCP allows it — the server suggests values for their arguments as
they are typed.

**Status: complete.** Schema 903, Core 264, UI 94 — all green; the NVDA pass found four defects that
the suite had not (§4), each fixed and re-checked by ear.

## 1. Decisions

- **A prompt is a second kind of operation, not a second window.** `ToolInfo` carries an
  `OperationKind`; a prompt is a `ToolInfo` of kind `Prompt`, built by `PromptForm`. The tool window,
  the tree's commands, the result view and F1 already took a `ToolInfo`, and a prompt is what a tool
  is to all of them — arguments in, content out. A parallel `PromptWindow` would have copied about
  four hundred lines of form logic (warn once, focus the first problem, the pane cycle, §7.25's caret
  rules) to change a dozen words. What differs is the request, which `ServerConnection.CallAsync`
  routes on the kind, and the words: *Get prompt*, *arguments*, *Getting*, the *Prompt* menu.
  `ToolInfo` keeps its name; it was already documented as "one invocable operation".
- **A prompt's arguments are written out as the JSON Schema they mean** — an object of string
  properties, `title`, `description` and `required` carried over — and handed to `FormBuilder`. Every
  argument is Tier 1 by construction, and labels, help, access keys, the optional toggle, required-
  field validation and the omission rule all follow without prompt-specific code. A nameless argument
  is dropped and a repeated one keeps its first spelling, because neither can be sent.
- **Messages are result parts with a speaker.** `ResultBlock.Role` is "User" or "Assistant" on a
  prompt's parts and empty on a tool's. The heading is *"User message 1 of 2"*, with the part's own
  heading after it when it is not text (*"User message 2 of 3, Image, PNG, 70 bytes"*); the
  announcement is *"Prompt fetched. 2 messages: user, 2 lines of text; assistant, 1 line of text."*
  A picture or resource in a prompt is the same control it is in a tool's result.
- **Completion is asked on focus and when typing pauses (300 ms)**, with what the other arguments
  hold as context, so a server can narrow one by another. The suggestions go first in the field's
  drop-down and this user's history after them. A value reached by arrowing through the list is not
  treated as typing, or the list being arrowed through would be replaced by one narrowed to it.
  **Nothing is announced when suggestions arrive**: the help text says the drop-down exists, opening
  it says what is in it, and a status line on every pause would talk over the typing.
- **Only a server that declares `completions` is asked**, and every argument of its prompts is a
  drop-down from the moment the form opens (`TextFieldState.HasServerSuggestions`), so no control
  changes kind under the user's hands. A failure to complete is a diagnostic, never a message.
- **History is filed under `prompt:<name>`**, because a server may publish a tool and a prompt with
  the same name — the fixture's `demo_echo` is both — and their fields are different fields. A colon
  cannot appear in a tool name. The same pair also keeps open forms and tree reselection apart.
- **A prompt states no safety.** Fetching one changes nothing on the server, but "read-only" on
  every prompt would be a word said on every arrow for no decision.
- **Prompts are not in tool search.** Phase 8's search is a search for tools, and its measured bar is
  over tools; whether prompts belong in it is a question for when a user has a server with many.

## 2. What was built

- **Core:** `OperationKind`; `PromptForm` and `PromptArgumentInfo` (tested without a server);
  `ServerConnection` lists prompts when the server claims them (a failed listing costs the prompts,
  never the tools), re-lists them on `prompts/list_changed` — subscribed through
  `subscriptions/listen` beside the tool list on 2026-07-28 — fetches with `prompts/get` (MRTR
  questions are routed exactly as for a tool call) and completes with `completion/complete`;
  `ServerSnapshot.Prompts`; the tree's Prompts category and the server node's count ("31 tools, 4
  prompts", prompts only when there are some); the list-changed announcement in either noun.
- **Schema:** `TextFieldState.HasServerSuggestions`; `AccessKeys.PromptCommands`.
- **App:** the *Suggestions* template (the history combo box with its own hint); the form window's
  words by kind; the suggestion requests; prompt nodes in the context menu and F1.
- **Fixture:** four prompts — none, completed arguments with context (`review_code`), a conversation
  with a picture, and one named like a tool — and a completion handler.

## 3. Measured against a real server

`@modelcontextprotocol/server-everything` (protocol 2025-11-25) publishes four prompts, two of them
completed. Its *Team Management* prompt is the case the context exists for: Department "Sales", then
Name offers David and Eve, and the fetched message is "Please promote Eve to the head of the Sales
team." Its *Resource Prompt* returns an embedded resource, which reads "User message 2 of 2, Embedded
resource, demo://resource/dynamic/text/1".

## 4. The NVDA pass

Four defects, each fixed and re-checked by ear:

1. **"Connected to 2 servers, 51 tools."** The several-servers sentence counted tools only, while the
   one-server sentence counted prompts too. Now "53 tools, 8 prompts".
2. **The prompt form reserved the tool form's access keys.** *Get prompt* is G and the *Prompt* menu
   P, neither reserved, so an argument called "Goal" would have shared G with the button (§7.21).
   `AccessKeys.PromptCommands` reserves P, W, G, C, O.
3. **An empty drop-down opened in silence.** Alt+Down on an argument the server had nothing for —
   server-everything's resource id before anything is typed — said "blank", straight after help text
   that promised suggestions. It now says "The server has no suggestions for Resource id yet. Type
   something and try again.", or that none match what is typed.
4. **The empty result pane of a prompt said "Results appear here when a tool runs."** It says the
   prompt's messages appear there when it is fetched. The main window's Run button and the tree's
   help text were tool-only in the same way and now name prompts.

Heard, and fine: the Prompts category and each prompt's argument count on arrow-through; the form's
"The server suggests values as you type"; "py", Alt+Down, "python"; the framework list narrowed by
the language; "Getting Review code." then the fetch announcement; F6 onto "User message 1 of 2" and
Tab to the assistant's; a zero-argument prompt fetched straight from the tree into the main window.

Noticed on the way and not this phase's: a `servers.json` that is not valid JSON is reported in the
status line, while the tree still says "No servers are configured" — true of the list humcp could
read, misleading about the file.

## 5. Not done here

- **The server's `total` and `hasMore` are not said.** A drop-down of the first hundred of more is
  presented as if it were all of them.
- **No copy of the whole conversation.** Each message is copied on its own (Control plus Shift plus
  C, or select all in it); a prompt's purpose is usually to be pasted somewhere, and one keystroke for
  the lot would serve that better.
- **No completion for tool arguments that look like resource URIs** (pitfall 3's second half). That
  needs resources, which are Phase 13.
