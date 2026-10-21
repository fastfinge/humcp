# Phase 8 — Tool search, with no model

v2's first phase (ROADMAP §11, "Built in this order"), started 2026-10-21. §8's filter box, which v1
never built, as a search across every connected server.

**Status: built, automated suite green; the NVDA pass is outstanding** and closes the phase.

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
  700 ms. Down Arrow or Enter moves from the box to the best match; Escape, or Up Arrow on the first
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

Not investigated further: stopping the developer's screen reader to find out is not an option, and
the NVDA pass checks the arrows by ear anyway.

## 4. NVDA checklist (outstanding)

1. Control plus F from the tree: "Find a tool, edit", with its help text.
2. Typing: letters echoed, then after a pause one "N tools match", never per keystroke.
3. Down Arrow into the results: the best match read with its server's name.
4. Up Arrow on the first result returns to the box; Escape from a result returns to the box.
5. Enter on a result with parameters opens its form; on one without, runs it, and the result pane
   heading names it.
6. F1 on a result describes the tool; Applications key gives the tool's context menu.
7. A search with no match: the sentence is reachable and read; Run tool is unavailable.
8. Escape from the box: search cleared, the tree back, the caret on a tree item that is read.
9. Tab order: the box, then the tree or the results, then Run tool, Reconnect, the result pane.
10. F6 with a search showing reaches the results, not the hidden tree.
