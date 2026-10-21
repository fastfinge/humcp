# Phase 8 — Tool search, with no model

v2's first phase (ROADMAP §11, "Built in this order"), started 2026-10-21. §8's filter box, which v1
never built, as a search across every connected server.

**Status: complete** (2026-10-21). Suite green; the NVDA pass found two defects the suite had not, and
one older than v2, all fixed and re-checked by ear (§4).

## 1. What was built

- **`ToolSearch`** (`src/Humcp.Core/ToolSearch.cs`): BM25 over each tool's server name, name (twice),
  title and the first 600 characters of its description. Words are split at punctuation and camelCase
  humps, stop words dropped, and cut to a rough stem (plurals, -ing, -ed, a final e) so "files" finds
  "file" and "creating" finds "create". The last word is a prefix unless the query ends in a space,
  because the box is searched as it is typed; a word part-way into its ending ("listi") still finds
  its stem.
- **The box and the list** (`MainWindow`): <kbd>Ctrl+F</kbd> (also Tool, Find a tool) reaches a box
  above the tree. While it holds text, a `ListBox` of `CatalogueNode`s takes the tree's place — tool
  nodes like the tree's, named with their server, so Run, F1, Copy tool schema and the context menu
  work on them unchanged through one `SelectedNode()`. The count is said once typing has paused for
  700 ms, naming the best match ("41 tools match. First: Server information, second fixture.").
Down Arrow or Enter moves from the box to the best match; Escape, or Up Arrow on the first
  result, goes back to the box; Escape from the box clears the search and returns to the tree, which
  a search never touched. Nothing matching is one focusable sentence, as an empty tree is.
- **The catalogue changes under a search**: a server reconnecting re-indexes and searches again,
  keeping the selected tool and the caret if it is still found (§7.25).

## 2. Measured

Against the 35 hand-written requests over the corpus's 213 tools (`tests/fixtures/toolsearch`, which
is evaluation data and is hand-written on purpose):

| | Right tool in top 3 | In top 15 |
|---|---|---|
| The spike's BM25 (v2-llm-spike §6) | 20 / 35 | 24 / 35 |
| Phase 8 | **21 / 35** | **27 / 35** |

`ToolSearchCorpusTests` holds 21 as a floor. The requests that share no word with their tool ("how
high above sea level is Denver" → `maps_elevation`, "make a new folder" → `create_directory`) are
where lexical search stops; they are Phase 11's. No synonym table was added: it would be tuned to
these 35 questions and measure nothing. A search of the whole corpus is well under a millisecond.

**`IToolSuggester` is not defined yet**, though the plan first said it would be: the model in Phase 11
is the second implementation, and its catalogue prefill decides the interface's shape (§12).

## 3. Findings

### 3.1 A synthetic arrow key never reaches humcp while NVDA is running

Down Arrow from the search box did nothing in the UI suite. A log of every key the main window
previewed showed Control, F, Enter and Escape arriving — and Left and Down Arrow never arriving at
all, not handled by anything, simply absent. NVDA was running, as it is on the development machine.
The arrows are the only extended keys the suite sends.

Two consequences:

- **The suite's one earlier arrow-key test passes vacuously.** `ResultTests`' grid test presses Down
  and asserts the caret did *not* land in the menu bar, which is just as true when the key never
  arrived. It has not been proving what phase4-notes 4.3 fixed.
- The search test moves from the box with Enter, which takes the same path; Down Arrow, and Up Arrow
  on the first result, are on the NVDA checklist below.

The same keys pressed through the screen reader bridge arrive normally (§4), so it is the suite's
injected input that is lost, not arrow keys as such. Not investigated further: stopping the
developer's screen reader to find out is not an option, and the NVDA pass checks the arrows by ear.

## 4. The NVDA pass

Driven through NVDA by the screen reader bridge, silent capture, *user* stance, against a scratch
profile holding two copies of the fixture server (so every tool exists on two servers and the server
name in a result is what tells them apart). Three defects; the suite had found none of them.

### 4.1 New words kept the old selection

Search "forecast", then type "server information" over it, and press Enter in the box: the caret went
to *Forecast as JSON text, 21 of 41* — still selected from the first search, and still matching the
second because its description says "most servers". The selection was kept across every rebuild of
the list, which is right when a server reconnects under the same words and wrong when the words
change, because Enter from the box goes to the selection and the user has every reason to expect the
best match. Now kept only when the catalogue changes; new words select the first result. Regression
test: `SearchTests.New_words_select_the_new_best_match`.

### 4.2 Run tool and Reconnect came before the tree — since Phase 1

Tab from the search box went to the results, then the result pane, then Reconnect: the buttons under
the tree were reached last, after the other pane, and before the search box above them. The pane was
a `DockPanel`, which declares what it docks at the bottom before what fills the middle, and tab order
follows the declaration — phase3-notes 4.3 exactly, fixed in the tool form in Phase 3 and never looked
for in the main window, where the tree and buttons had been in the wrong order for seven phases.
Phase 8 made it visible by adding something above the tree that also came after the buttons. The pane
is a grid of rows now. The tab-order test asserted only that every control was reached, which is why
it passed; it now asserts the order as well, and fails on the old layout.

### 4.3 "41 tools match" said nothing useful

The search is ranked rather than filtered, so any shared word counts — "server" matches every tool on
a server whose name contains it. A count alone told the user nothing about whether the words had
worked, and the first result was exactly right. The announcement now names the best match, so the
words can be judged from the box.

Also found, not changed: the pane sized itself from the tree's widest line, and a hidden tree measures
nothing, so a resize during a search would have shrunk the pane to its minimum. Fitting now waits for
the tree to come back.

### 4.4 Heard, and fine

| Check | Heard |
|---|---|
| Control plus F | "Find a tool, edit", help text, "Control plus F" |
| Typing | letters echoed, then one count after the pause, never per keystroke |
| Down Arrow into results | "Matching tools, list", then "Forecast as JSON text, second fixture, read-only, 1 optional parameter … 1 of 2" |
| Up Arrow on the first result, Escape from a result | back to the box, text selected so typing replaces it |
| Enter on a result | with parameters, its form opens; without, it runs and the result is announced; closing the form returns to the result it came from |
| F1, Shift+F10 | details window; Run, Details, Copy tool schema |
| Nothing matches | "No tools match xylophone. Escape goes back to the search box. 1 of 1"; Run tool unavailable |
| Escape from the box | search cleared, "Servers and tools, tree view", the server node read |
| Tab, after the fix | box, results, Run tool, Reconnect, result pane |
| F6 with a search showing | the results and the result pane, not the hidden tree |

The context menu opens as "menu, menu" with nothing selected until Down Arrow. The tree's does the
same, so it is WPF's and older than this phase; recorded rather than changed here.
