# Phase 14 — Tasks

v2's seventh phase (ROADMAP §11, "Built in this order"), 2026-10-23. A server that offers the
`io.modelcontextprotocol/tasks` extension (SEP-2663) can answer a tool call with a task rather than a
result; humcp asks after it until it ends, answers its questions, stops it when asked, and lists every
task of the session in a window of its own, Background tasks, reached with <kbd>Ctrl+T</kbd> from
every window.

**Status: complete.** Schema 903, Core 307, UI 104 — all green; the NVDA pass found three defects
that the suite had not (§4), each fixed and re-checked by ear — two of them twice, because the first
fix did not hold.

## 1. Decisions

- **The SDK's own extension package, not the protocol by hand.** `ModelContextProtocol.Extensions.Tasks`
  2.2.0 ships with Core 2.2.0 and pins it exactly; its client calls are `CallToolAsTaskAsync`,
  `GetTaskAsync`, `UpdateTaskAsync` and `CancelTaskAsync`. It depends on the SDK's main
  `ModelContextProtocol` package, which brings seven `Microsoft.Extensions` abstractions with it — all
  MIT, from dotnet/runtime, and in `THIRD-PARTY-NOTICES.md`.
- **Offered per request, to a server that claims it.** The extension is declared in each request's own
  `_meta` (the SDK does this, and its meta injection merges rather than overwrites), and only on
  2026-07-28 to a server whose capabilities list the extension. The server decides which calls become
  tasks — a tool's listing says nothing about it — so every call to such a server is offered one.
- **`CallToolAsTaskAsync` goes through `SendRequestAsync`, which is where the SDK resolves
  `input_required`.** A server that asks a question before deciding to make a task is answered exactly
  as on a plain call; read in the SDK's source rather than assumed.
- **Polling, at the server's suggested interval held between a quarter of a second and half a minute.**
  The SDK does not yet deliver the extension's status notifications (its own documentation says so).
  The first ask comes after 200 ms, since most calls a server makes tasks of are quick. Three failed
  asks in a row, the connection ending, or the server forgetting the task make it **lost**, said in a
  sentence.
- **A task's questions use the same handler as a call's.** `ResolveInputRequestsAsync` dispatches each
  `inputRequests` entry through the registered elicitation handler, and the answers go back with
  `tasks/update`. Each key is answered once, as the extension asks. Where a question goes is the task's
  `Prompter`, read when the question arrives rather than when the task began, because the window that
  started it may have closed since.
- **Stopping and not waiting are two actions.** For a call they were one: nothing can be waited for once
  its window has gone. For a task, Escape (or Stop) asks the server with `tasks/cancel` and goes on
  waiting to hear how it took it; closing the window stops only the waiting, and the work carries on.
  `CallAsync`'s token keeps its old meaning until a task exists and means "stop waiting" after.
- **A form waits for its task**, and shows the result in place, as for any call. The **main window**
  waits three seconds and then hands a task still running to the background list, because a tool with
  no form has nowhere else to wait and waiting there would keep every other such tool from being run.
  It does not hand over while the task is asking a question.
- **A task nobody is waiting for is announced where the user is.** "Where" is whichever humcp window has
  the focus, if it has a status line (`IStatusLine`), else the main window; and through prism as well
  when no humcp window has the focus and the user asked for that, exactly as before. The waiting window
  says it itself when it is the one being read.
- **The list says clock times** — "started 5:23:35 PM, ended 5:23:47 PM" — rather than "two minutes
  ago", which would be wrong a minute later in a list nobody refreshed.
- **Closing humcp with tasks running warns once and closes on the second asking**, as a second Delete
  removes a server: a stdio server goes with humcp, and nothing is left waiting for an HTTP one.
- **The fixture runs only its four `task_` tools as tasks.** The SDK's `WithTasks` makes every tool a
  task once a client offers, and the other thirty-two are the subject of tests written for calls that
  answer at once. The stdio fixture is built without dependency injection, so the extension's one
  `IConfigureOptions` is applied to its bare options by hand (`Program.Tasks`).

## 2. What was built

- **Core:** `BackgroundTask` (state, status line, outcome, `Completion`, `StopAsync`, `Describe`,
  `Ending`), tested without a server; `ServerConnection.SupportsTasks`, the task path through
  `CallAsync` with its `becameTask` callback, `PollAsync`, `AnswerAsync`, `StopTaskAsync`, and
  `PollInterval`. A task's progress token is kept until the task ends, since its progress goes on after
  the call that created it has answered.
- **App:** `BackgroundTasks` (the list, the hand-over, the endings), `TaskListWindow` (the list and the
  selected task's result in a `ResultView`, F6 between them, Delete stops), `WindowManager.ShowTasks`
  (<kbd>Ctrl+T</kbd> and a first entry in every Window menu), the form's and the main window's task
  handling, and the close warning.
- **Fixture:** `TestTasks` — a five-second report with progress and nothing to fill in, a wait of as
  many seconds as asked, a question in the middle, and a protocol error — and `WithTasks` over both
  transports. 36 tools now.
- **Tests:** Core round trips over stdio (result, progress, plain calls untouched, stop, not waiting,
  a question answered and one answered by whoever took the task over, failure) and one over HTTP; the
  task's own words without a server; six UI tests.

## 3. Against a real server

None. `@modelcontextprotocol/server-everything` speaks 2025-11-25 to humcp and its tasks are the
earlier experimental shape, which the SDK says is incompatible at both ends; no published server
offering the 2026-07-28 extension was found. The fixture is the only server this phase has run
against, which is worth re-checking when one appears.

## 4. The NVDA pass

Three defects, each fixed and re-checked by ear:

1. **The news that a task had gone to the background was cut off by the news that it had finished.**
   The first version said "Write a slow report is still running, in the background. humcp will say when
   it has finished; Control plus T lists background tasks." after a second and a half, and a
   three-second task finished 1.6 seconds later, long before the sentence had been spoken. The same in a
   form. Moving the hand-over to three seconds only moved the collision to three-second tasks — heard
   that way on the re-check, a tenth of a second apart. What held: one short sentence ("… is running in
   the background. Control plus T lists it."), the form's notice after three seconds rather than 1.2,
   and an ending held back until three seconds after the hand-over, so the two are three seconds
   apart whatever the task's length.
2. **Closing Background tasks put the caret in the main window**, not back in the form
   <kbd>Ctrl+T</kbd> was pressed in — Phase 13's defect in a new window. It is owned by the main window
   so that it outlives any form, and closing an owned window hands the foreground to its owner. The
   first fix activated the form while the list was closing, and was heard landing in the tree anyway:
   Windows gives the foreground to the owner once the window has gone, and WindowManager's own closing
   handler had already made the list inactive before the new one could see that it was active. What
   held: note "was active" before `Track`'s handler runs, and activate the form after the close.
3. **Pressing the Run button lost the caret.** Run disables itself under the caret, NVDA said
   "unavailable", and focus fell to the window's title (§7.25) — true of every form since Phase 3, and
   only reached now because a long call gave time to hear it. The form already put the caret back on
   Run when Stop disabled at the end of a call; nothing did the reverse. The first fix moved the caret
   before WPF had re-evaluated the buttons, when Stop was not yet enabled, and changed nothing. What
   held: move it when Stop becomes enabled, whichever button WPF updates first.

Heard, and fine: "Background tasks window", the list's help, "Write a slow report on fixture, finished,
started 5:16:32 PM, ended 5:16:35 PM. 1 of 1", "2 tasks, 1 running."; the tab order list, result,
list, with Stop absent while there is nothing to stop; "Stopped Work in the background." a third of a
second after Escape; a task's question in its own window, with the main window not handing over
under it and the answer's result in the result pane; a failed task's "… failed on the server. The
archive this task needed is missing."; and the close warning on the first Alt+F4.

Seen once and not again: right after a fresh start, "Running Write a slow report." came fifteen
seconds after Enter. Three later fresh starts answered at once. Not investigated further.

## 5. Not done here

- **No status notifications.** Polling only, until the SDK delivers `notifications/tasks`.
- **Tasks are not kept across restarts.** A stdio server's tasks end with it anyway; an HTTP server's
  could be asked after again, and are not.
- **Only tool calls.** The extension can augment other requests; the SDK's client offers it only for
  `tools/call`.
- **A server that ignores `tasks/cancel` keeps the form waiting** until it finishes; closing the window
  stops the waiting.
- **"Tool returned an error" opens a failed task's announcement**, which is a little wrong for a protocol
  error; the sentence after it says what happened.
