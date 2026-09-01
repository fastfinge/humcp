# Phase 5 — MRTR and elicitation

**Goal:** declare the `elicitation` capability; handle `resultType: "input_required"` — build the
form from `requestedSchema`, honour accept, decline and cancel, echo `requestState` verbatim, retry
with a new JSON-RPC id; decline sampling requests cleanly (ROADMAP §10).

**Exit:** a multi-step server tool completes through the GUI.

**Status: built, and its manual pass walked.** 1,046 tests green, 70 of them over the live UIA
tree of the running application. **The manual pass found ten defects that the suite's 1,042 green
tests had missed** (§4) — two of them older than this phase — and re-checking the fixes by ear
turned up the tenth. All are fixed, and each has a test.

Environment: .NET SDK 10.0.400, Windows 11 (10.0.26340), `ModelContextProtocol.Core` 2.2.0,
`JsonSchema.Net` 9.4.0, `FlaUI.UIA3` 5.0.0, xunit.v3 3.2.2, NVDA 2026.1. No new package references.

---

## 1. What exists

```
src/Humcp.Core/
├─ Elicitation.cs               ElicitationRequest / Answer / Prompter, and the one refusal
└─ ServerConnection.cs          declares form-mode elicitation; maps a question to a FormModel

src/Humcp.Schema/
├─ FormModel.cs                 FormModel.Empty — a question that is only a question
├─ FormState.cs                 Create(form, reserved): a window can say which letters it has spent
└─ AccessKeys.cs                QuestionCommands, the five letters this window's own chrome takes

src/Humcp.App/
├─ ElicitationWindow.cs         the question, the generated form, and the three answers
├─ ElicitationCommands.cs       Accept and Decline (cancel is "close the window")
├─ FormCommandBindings.cs       the commands the field templates raise, shared by all three
│                               windows that host a form (§4.9)
├─ FieldDatePicker.cs           a composite that carries its properties to the part with focus (§4.3)
├─ FieldDetails.cs              F1's text, moved out of ToolWindow so every window shares it
├─ FieldTemplates.xaml          the list rows are Grids now, so tab order matches the screen (§4.9)
├─ ToolWindow.xaml.cs           passes a prompter into the call
└─ MainWindow.xaml.cs           the same, for a tool with no parameters of its own

tests/Humcp.TestServer/
└─ MrtrTools.cs                 seven tools, one per shape a round trip can have
```

The dependency rule holds. `Humcp.App` still does not reference the MCP SDK: an
`ElicitRequestParams` becomes an `ElicitationRequest` carrying a `FormModel` before it crosses out
of `Humcp.Core`, and an `ElicitationAnswer` becomes an `ElicitResult` on the way back. The App never
learns that "elicitation" is spelled with a `mode` and a `requestedSchema`.

**No second form generator was written, and none was needed.** An elicitation's `requestedSchema` is
JSON Schema, so it goes through the same `FormBuilder`, binds to the same eleven templates, and is
filled in through the same `FormState` as a tool's own parameters. §3.2 is the measurement that says
that is not merely convenient but correct.

---

## 2. What a question looks like

A tool call that stops to ask opens a window of its own — non-modal, tracked, in every other
window's Window menu, out of Alt+Tab (§8), like everything else humcp opens.

| Part | What it is |
|---|---|
| The server's message | a read-only, caret-navigable `TextBox` named "What the server is asking" (§7.9, §7.28) — **and where focus starts** |
| The fields | the generated form, all of them, in a `FormItemsControl` (§7.12–13, §7.6) |
| Problems | the same error-summary list as a tool form (§7.5), shown only when there are any |
| Accept | sends what is on the form. <kbd>Ctrl</kbd>+<kbd>Enter</kbd> |
| Decline | tells the tool no. The tool carries on and decides what that means |
| Cancel | dismisses it. <kbd>Escape</kbd>, <kbd>Ctrl</kbd>+<kbd>W</kbd> and the title bar all do the same |

Three things about that table are decisions rather than layout:

**Focus starts on the message, not on the first field.** What the server is asking is the only thing
on the window that cannot be worked out from anything else on it, and a form whose question was
never read is a form answered blind. This is §7.28 — a part that has something to say needs a
focusable control to say it — applied to the one window where the *whole point* is a sentence a
third party wrote.

**Closing is cancelling.** The call is waiting on this window, so every way out of it has to be an
answer. A window that could be closed without answering is a tool call that never comes back, which
is the version of this bug with no symptom at all.

**Nothing is hidden.** A tool form starts with its optional parameters collapsed (§4, pitfall 4).
This one must not, and §3.3 is why.

---

## 3. Findings that change later phases

### 3.1 The SDK drives the whole round trip, and a question is not a result

The roadmap's Phase 5 entry describes the work as: handle `resultType: "input_required"`, gather the
input, retry the original call with `inputResponses` and a new id. **None of that is code humcp
writes.** `ModelContextProtocol.Core` 2.2.0 wires an MRTR interceptor into `tools/call`,
`prompts/get` and `resources/read`: an `InputRequiredResult` is resolved through the client's
registered handlers and the original request is retried, all inside one `await` on `CallToolAsync`.
A call that took three round trips arrives at `ServerConnection.MapResult` as one complete answer,
and the `resultType` guard written in Phase 4 for `input_required` can never fire again.

Measured against the fixture rather than read out of the documentation: `mrtr_book` asks two
questions and returns a booking, `mrtr_state_only` completes after two silent round trips, and in
both cases `CallToolAsync` returned once, with `resultType: "complete"`.

That is the good news. The consequence is the interesting part. **The handler is registered per
connection, but a question belongs to the window whose call provoked it** — one server can have
several tool windows calling at once, and a question that opens over the wrong one is worse than no
question. The SDK's handler signature carries nothing to correlate on.

What carries it is an `AsyncLocal<ElicitationPrompter?>` set inside `CallAsync` before the await.
The handler runs on a pool thread, but inside the calling flow, so it reads the value the caller
set; two calls in flight on one connection each see their own. That was measured too — a probe set
an `AsyncLocal` around `CallToolAsync` and read it back inside the handler on thread 9 from a caller
on thread 4 — and there is a test that keeps two questions outstanding at once and asserts each
window got its own.

> **For §9:** an `AsyncLocal` is how you correlate a per-call callback with a per-connection
> handler, and it is only correct because the SDK resolves input requests inline. If a future
> revision moves resolution onto the message-processing loop, this breaks silently and the fix is a
> correlation id from the SDK.

### 3.2 The elicitation subset really is a subset of Tier 1 — now measured

§3 of the roadmap claims: *"Our Tier 1 renderer is exactly this subset, so it is guaranteed to cover
100% of elicitation requests."* That was written from the 2025 spec text, which described the subset
as string, number, integer, boolean and enum with `enumNames`.

The 2026-07-28 subset is bigger than that, and two of its members do not obviously belong in Tier 1.
`ElicitRequestParams.PrimitiveSchemaDefinition` has **eight** concrete shapes, and the two newest —
the *titled* enumerations that replace the deprecated `enumNames` form — arrive on the wire as
composition keywords:

```json
"urgency": { "type": "string", "title": "Pick one, labelled",
             "oneOf": [ {"const": "p0", "title": "Drop everything"}, … ] }

"regions": { "type": "array", "title": "Pick several, labelled",
             "items": { "anyOf": [ {"const": "eu-west-1", "title": "Ireland"}, … ] } }
```

`oneOf` and `anyOf` are §5's Tier 3 list. A renderer that read that list literally would show a
server's "pick one of three" as a JSON editor — on the one form the spec authors explicitly designed
to be renderable.

**It does not, and the measurement says so.** `mrtr_kinds` publishes one field of every one of the
eight shapes, built through the SDK's own types and serialised the way the wire carries them, and
all thirteen fields come out **Tier 1, with no degraded reason on any of them**, with the option
labels intact: "Drop everything", "Ireland", "Ascending".

The reason is `FormBuilder.ConstantUnion`, written in Phase 2 for a different purpose — a union
every branch of which is pinned to one value is an enumeration wearing a different hat. It was built
because real tool schemas spell labelled enumerations that way; it turns out to be the thing that
makes the elicitation subset render natively. Two independent uses, one rule, and neither was
written for the other.

> **For §3 and §5:** the claim can stop being a claim. Say instead: *the elicitation subset is
> measured to land wholly at Tier 1, including the two titled-enum shapes that reach the client as
> `oneOf` and `anyOf` of constants; the constant-union promotion is what makes that true, and it is
> load-bearing for elicitation as well as for tool arguments.* Note the distinction from the
> decision that is **not** being reversed: `oneOf` between different *shapes* stays at Tier 3, and
> the `discriminator` promotion measured against the corpus in Phase 2 stays unbuilt.

### 3.3 A default the user never saw is sent as though they chose it

The confirmed rule (§5, "what a filled-in form means") is that **a value the user did not choose is
not sent**. `FormState.Prepare` honours it here exactly as it does for a tool's arguments: a form
where only `plain` was typed into produces the argument object `{"plain": "typed"}`.

That is not what leaves the process.

```
humcp asked the SDK to send:  {"plain":"typed"}
what arrived at the server:   {"action":"accept",
                               "content":{"plain":"typed","count":7,"agreed":true,"size":"medium"}}
```

`count`, `agreed` and `size` are the three properties of that schema that state a `default`. **The
client SDK writes every schema-stated default back into the elicitation content on its way to the
wire.** Confirmed at the far end rather than inferred: the fixture prints
`InputResponse.RawValue`, so what is being read is the JSON as it arrived, not something a
deserialiser reconstructed.

Is the SDK wrong? Arguably not, *for elicitation*. A tool's optional argument left alone means "use
your own logic"; an elicitation form's prefilled box means "this is the value, unless you change
it", and a user who presses Accept with it on screen has agreed to it. The reading is defensible.

But it is only defensible **if the box was on screen**. And humcp's tool form hides optional fields
by default — 491 of the corpus's 785 top-level parameters are optional, and hiding them is the whole
measured benefit of §4's pitfall 4. Carry that habit into this window and the user accepts three
values they were never shown, and the server is told they chose them.

So: **an elicitation form hides nothing.** No optional toggle, nothing collapsed, every field on
screen and in the tab order — `ElicitationWindow` binds the form to `FormState.Fields` where a tool
window binds it to `VisibleFields`, and that one word is the whole difference. The rule is not "elicitation forms are small" — it is that a field
which will be sent whether or not it is shown must be shown.

> **For §5:** add the exception, and the reason. The omission rule is humcp's, it survives inside
> `Humcp.Schema`, and it does not survive the client SDK for elicitation content. The window
> compensates by showing everything rather than by fighting the SDK.
>
> **For §7 (proposed 7.31):** *a field whose value will be sent whether or not it is on screen is a
> field that must be on screen.* Hiding is only honest where hiding also means omitting.

### 3.4 Declining is a handler that refuses, not a handler that is missing

humcp has no language model (§2) and publishes no roots; both are deprecated in 2026-07-28 and
neither capability is ever declared. A server can ask anyway, and the roadmap asks for that to be
declined *cleanly*.

Leaving the handlers unregistered is not clean. The SDK throws
`InvalidOperationException: Server sent a sampling input request, but no SamplingHandler is
registered` — out of `CallToolAsync`, into `ServerConnection`'s catch-all, and onto the user's screen
as a sentence about handler registration. Every message humcp shows is spoken; that one is about the
inside of a library.

So both handlers **are** registered, and both throw `UnsupportedInputRequestException` with a
noun-phrase saying what was wanted. `CallAsync` catches it by type and produces:

> *Ask for a language model asked humcp for a language model, which it does not do, so the call
> stopped there.*

Catching by type rather than by message is the point: the refusal is ours, so the sentence is ours,
and a change in the SDK's wording cannot turn it back into jargon. It also means that referencing
two deprecated APIs is deliberate — a client that stops compiling against a deprecated request is a
client that stops being able to turn one down.

### 3.5 A round trip that asks nothing is invisible, and the cap is not ours

SEP-2322 allows a `requestState`-only response with no `inputRequests` at all — load shedding, a
server saying "come back in a moment". The client must retry with the state echoed and nothing
added. Nothing is shown to the user; the call simply takes more round trips.

Which means **humcp cannot count them**. The elicitation handler is never called, so the only place
that could apply a limit never runs. A server that answers every retry with another `requestState`
is a client-side infinite loop with no UI and no symptom.

It is bounded, but not by us: the SDK stops at ten and throws
`McpException: Server returned InputRequiredResult more than 10 times`. Measured — 200 requested
rounds ended in 24 ms.

Two consequences. The message is rewritten into humcp's own words, keyed on the SDK's sentence,
which is as brittle as it looks and deliberately harmless: when the wording changes the user gets a
worse sentence, not a worse outcome, and there is nothing better to key on — the exception carries
no code of its own. And the cap belongs on the list of things that are the SDK's rather than ours,
so a future SDK that removes it silently removes humcp's only protection.

> **For §11, Phase 7:** *cancellation* is what makes a long or looping call survivable, and it is
> already listed there. This is a second reason for it: the ten-round cap is the SDK's, not humcp's,
> and the user's own way out should not depend on somebody else's constant.

### 3.6 Elicitation has two modes, and the roadmap only knew about one

§3 describes elicitation as a form. In 2026-07-28 it is a form **or** a URL:
`ElicitRequestParams.Mode` is `"form"` or `"url"`, and url mode asks the client to send the user out
to a web page — an OAuth flow, a payment, a credential — precisely so that the client never sees
what is typed. `ElicitationCapability` has a slot for each, and a client must support at least one.

**humcp declares form mode only.** Three reasons, in order of weight:

1. url mode's own purpose is the OAuth-shaped problem that §11.3 defers to v3, complete with its
   security invariant. Declaring it here would mean shipping the user-facing half of a flow whose
   dangerous half is explicitly not built yet.
2. Honouring it means handing a server-chosen address to a browser as a side effect of running a
   tool. That is the exact shape `LinkPolicy` exists to gate, and `LinkPolicy` gates a link the user
   asked to follow — not one a tool call decided to follow on their behalf.
3. Completion arrives out of band, as a notification correlated by `ElicitRequestParams.
   ElicitationId`, which needs the notification handling Phase 6 owns.

A capability that is not declared is still a request that can arrive, so it is handled rather than
ignored. A url-mode request opens the same window with no Accept button: the server's message, a
sentence in humcp's own words saying that it does not open a web page as part of running a tool, and
the address itself in a read-only box that can be read with a caret and copied — and no control
anywhere that would follow it. Decline and Cancel are the two answers.

> **For §3:** elicitation has two modes. For **§11, v3:** url mode is where OAuth would actually
> surface to the user, and the decision to decline it is worth revisiting there and nowhere earlier.

---

## 4. What the manual pass found

**Ten defects, and the automated suite had found none of them.** That is the third phase running,
and the count is going up rather than down.

**Five of the ten were in code older than this phase**, which is the more useful number: a duplicated
"read only" that has been in the result box since Phase 4 and in the details window since Phase 1; a
result pane that dropped the caret when a tool was run again from inside it; a date field that never
said it was required; a form with nothing visible on it that focused nothing; and an Add button
declared before the box it adds from. Two of those five *could not* have been found earlier — until
an elicitation schema asked for a required date, and until a tool existed whose only parameter was
optional, neither path had ever been walked. The other three had been walked and not listened to.

All ten are fixed, each has a test, and every fix was re-checked by ear — which is where the tenth
came from.

The exit criterion was met before any of them were fixed, by keyboard alone through two generated
question windows with a screen reader reading every step:

> Booked.
>
> Destination: Perth
> Departure:   "2026-01-09"
> Passengers:  2
> Flexible:    false
> Seat:        "window"
>
> Two round trips, and the server remembered nothing between them.

`Flexible: false` is the check box nobody touched, put back by the client SDK on its way out — §3.3
heard rather than measured.

### 4.1 "Read only" said twice

> *"What the server is asking, edit, **read only**. **Read only.** This is the server's own
> wording. Nothing will be deleted…"*

The reader announces the read-only state from the control. Help text that opens by saying it again
is the same word twice — §7's core rule, one register down: not "is it on screen?" but "has the
reader already said this?"

Not new, and not only here. The Phase 4 result box has said *"Result, edit, read only. Read-only.
Arrow keys review the text line by line"* since Phase 4, and the details window has said its version
since Phase 1. Nobody heard it because nobody was listening for the first three words. Fixed in all
four places; what is left is the half that is worth saying.

### 4.2 The second question was announced exactly like the first

Answering the first question of `mrtr_book` opens the second, and both were *"Question — Book a
journey window"*. The message differs and is read a second later, but for that second there is
nothing to tell a user they have moved on rather than failed to.

Now the first is *"Question"* and any later one is *"Question 2"*. Numbered from the second onwards
rather than from the first, because the number exists to say *this is a new one*, which is only
information once there has been a previous one.

### 4.3 A required date did not say it was required

> *"Departure date, edit. The day the journey starts…"* — beside *"Passengers, edit, **required**…"*

Both are required by the same schema. `get_focus_info` confirmed the gap rather than a mishearing:
the focused object reported `FOCUSABLE, FOCUSED` and no `REQUIRED`.

**A `DatePicker` is a composite.** Focus lands on `PART_TextBox` inside it, and a reader reads that
box's properties. Name and help text survive by peer fallback; `IsRequiredForForm` and the access
key do not. Nothing about that is specific to dates — it is what happens to any property set on a
control whose focus lands on one of its parts.

`FieldDatePicker` now copies them onto the box. The access key is the stubborn half: it is worked
out inside the picker's peer, and neither setting `AutomationProperties.AccessKey` on the box nor
re-pointing the `Label`'s `Target` at it made a reader say the letter — both were tried. The key
itself works (<kbd>Alt</kbd>+<kbd>E</kbd> lands in the box), so what is left is §7.15's own case: a
keystroke nothing announces is one the renderer spells out. The help text now ends *"Alt plus E
moves here"*, next to the Alt+Down Arrow hint that is there for the same reason.

This is the first required date this project has ever had. The corpus contains no parameter using
`format: date` at all (phase2-notes 3.4), and the fixture's only other one is optional — so the
control has been in the app since Phase 3 and this could not have been found until an elicitation
schema asked for one. `DateFieldState`'s own comment predicted it: *"Phase 5 will meet one."*

### 4.4 The answer ate the result

> *"Declined. Book a journey is still running."* — and then nothing, ever.

The call had finished. *"Tool call complete, 9 lines of text"* was never spoken, and the only way to
find out the tool had answered was to go and look.

Answering the last question of a call and the call completing are milliseconds apart, and the second
announcement was lost behind the first and the focus change between them. A tool with no questions
announced its completion perfectly, which is what identified the cause.

"Still running" is only worth saying while it is still running, so it now waits 1.2 seconds to find
out and says nothing if the call has finished. That is an announcement policy, which §10 assigns to
Phase 6 — this is the one instance of it Phase 5 created and therefore owed.

### 4.5 Running a tool from the result pane lost the caret

<kbd>F6</kbd> to the result, <kbd>Ctrl</kbd>+<kbd>Enter</kbd> to run again: the pane is rebuilt, the
focused control is destroyed, and WPF drops focus to the window, where a reader says the window's
title and stops. §7.25 exactly, in the one place nobody had tried it — Phase 4's pass ran every tool
from the form.

Both result panes now put the caret back, and only when the rebuild really lost it: restoring focus
to a control that still has it makes the reader announce it a second time, which was audible in the
first attempt at this fix.

### 4.6 A form with nothing on it left the caret nowhere

`mrtr_state_only` has one parameter, optional, and therefore hidden — so `FirstToFill` had nothing to
return, `FocusField` returned false, and the window opened with focus on itself. §7.25's rule was
written as *"focusing a field that turns out to be a group means focusing the group's first member,
never giving up quietly"*; this is the same rule with no field at all. The caret now goes to the
optional-parameters toggle, which on such a form is the only thing there is to do, and to the Run
button if there is not even that.

Also older than this phase: any tool with only optional parameters would have done it, and the
fixture had none until now.

### 4.7 humcp's own sentence was cut in half

> *"humcp does not open a web page as part of running a tool, so this request cannot be accepted.
> **Decline tells the tool**"* — and the rest, *"so and lets it carry on…"*, only on the next arrow
> press.

A wrapped read-only box is read to the end of its first *visual* line on focus. For a server's
message that is right and expected — it is prose, and a caret is how prose is reviewed (§7.9). For a
sentence humcp wrote about its own behaviour it is not: that has to be heard in one go, or it is not
an explanation. It is now one short sentence. The address beside it stopped wrapping instead, since
an address is one line by nature and wrapping it was what split it in two.

### 4.8 A live region has no quiet mode

The fix for a redundant announcement — *"One field of each kind is asking for more information. A
window has opened."* immediately before the window announced itself — was to write the status line
without raising the live-region event.

**It made no difference.** The reader announced it anyway, because it watches the region's *text*
and not the event the application raises. So `SetStatus` has one mode, it is "spoken", and a message
that must not be spoken has no business being one. The status line is gone; the window says what it
is, which was always the better sentence.

The `announce: false` parameter was written, measured, and taken out again. It is recorded here
because the next person to want one will have the same idea.

### 4.9 The question window bound none of its own commands

The elicitation subset has two multi-select shapes, so a generated question form really does contain
lists. The window bound Accept, Decline and Details — and none of the four commands its own field
templates raise.

A `DataTemplate` has no code behind it, so a button inside one asks by command and the window
answers. A window that does not answer has buttons that are permanently disabled, **and a disabled
button is not in the tab order at all** — so the box announced *"choose a value and press Add to put
it in the list below"* beside a button that could not be reached, and nothing said so. The same was
true of the sub-form window an object list opens: a routed command bubbles up the tree it was raised
in, and that is a window of its own, so the owner's bindings never saw it either.

Two more defects came out of fixing it, both older and both about the same list:

- **The Add button was declared before the box it adds from.** `DockPanel.Dock="Right"` draws a
  control on the right and declares it first, and tab order follows the tree rather than the screen —
  which is phase3-notes 4.3 in a new place. So a user who did as the help text said and chose a
  value found that Tab went past the button, which was behind them. Every list row is now a `Grid`
  with the box first.
- **`CanExecute` had not been asked again by the time Tab decided.** WPF re-evaluates on input and
  on idle, not when a view-model raises `PropertyChanged`, so the button was still disabled at the
  instant the choice was made. `Humcp.App` now invalidates the command manager when a list's
  `CanAdd` changes — `Humcp.Schema` raises the notification and has never heard of WPF (§6.1), so
  what it means to a command system is known only in the App.

### 4.10 And one the re-check found

Pressing Add empties the draft, which makes `CanAdd` false, which disables the button the caret is
standing on — and WPF drops focus from a disabled control to the window. So the fixed Add button
worked once and then put the caret on the window, where a reader says the title and stops:

> *"Added to Pick several, unlabelled. 1 entry now." — "unavailable" — "Question, One field of each
> kind, window"*

The caret now goes back to the box beside it, which is where the next value would be typed anyway.
**A fix is not finished until it has been listened to**, which is phase4-notes 4.9's sentence, found
again by doing what it says.

### 4.11 What the pass confirmed

- The whole elicitation subset reads correctly by ear as well as by assertion: *"Pick one, labelled,
  combo box, Not set, collapsed, required, L"* with the options *"Drop everything"*, *"This week"*,
  *"Whenever"* — a `oneOf` of constants, arriving as a combo box with the server's own labels (§3.2).
- The tab order in a question window is the message, the fields in schema order, and the three
  answers — nothing else, and it cycles.
- Every way out answers: <kbd>Escape</kbd> and the Cancel button both reach the server as
  `"cancel"`, and the tool says so. <kbd>Ctrl</kbd>+<kbd>W</kbd> and the title bar are the same code
  path — closing the window *is* cancelling — but only the first two were pressed by hand.
- Declining sampling reads as a sentence about the tool rather than about a library: *"Tool returned
  an error. Ask for a language model asked humcp for a language model, which it does not do, so the
  call stopped there."*
- Three silent round trips are genuinely silent: `mrtr_state_only` announces *"Running"* and then
  *"Tool call complete"*, with nothing in between (§3.5).
- The two-round booking completes and reads back everything both rounds collected — including
  `Flexible: false`, a default nobody touched, which is §3.3 heard rather than measured.

---

## 5. Decisions taken

- **The prompter is per call, not per connection.** A question belongs to the window that provoked
  it (§3.1).
- **`ElicitationWindow` shows every field.** The one place it departs from a tool form, and §3.3 is
  the measurement that requires it.
- **Accept, Decline and Cancel are the protocol's three words**, not softened into "Send" and
  "Don't". They are three different things — declining tells the tool something, cancelling tells it
  nothing — and a window whose buttons are named for the answer they give is one where the fixed
  access keys <kbd>Alt</kbd>+<kbd>A</kbd>, <kbd>Alt</kbd>+<kbd>D</kbd> and <kbd>Alt</kbd>+<kbd>C</kbd>
  mean the same thing on every question a user ever sees.
- **Cancel is `WindowManager.CloseWindow`.** One code path for the title bar, <kbd>Escape</kbd>,
  <kbd>Ctrl</kbd>+<kbd>W</kbd> and the button, so they cannot disagree.
- **Validation warns once and then sends**, exactly as on a tool form (§5). The server is the
  authority here for the same reason it is there, and the only refusal is still unparseable JSON in
  a Tier 3 editor — which an elicitation schema cannot produce, but the code path is shared and
  costs nothing.
- **`FormState.Create` takes the letters its window has already spent.** A question window's chrome
  claims `q`, `w`, `a`, `d`, `c`; a tool form's claims `t`, `w`, `r`, `c`, `o`. A single longer
  shared list would have cost every tool form two letters it never needed (§7.21).
- **`FieldDetails` is shared.** <kbd>F1</kbd> on a field says the same thing in both windows,
  because a habit that stops working at a window boundary is a habit nobody keeps.
- **The fixture's MRTR tools are one per shape**, not one per scenario — the same rule the `result_`
  tools follow: two rounds, a question with no fields, every schema kind at once, state with no
  question, sampling, roots, and url mode.
- **`FormCommandBindings` is shared by every window that hosts a form**, and that is the fix for
  §4.9 rather than a tidy-up after it. The question window was written by copying what a form window
  needs and missing four commands; the sub-form window never had them at all. A rule that has to be
  remembered in three files is a rule that will be forgotten in two of them — which is
  `FormItemsControl`'s own comment, proved again.

---

## 6. What Phase 5 deliberately does not do

- **No url-mode elicitation** (§3.6). Declined, explained, and revisited in v3.
- **No sampling and no roots** (§3.4). Refused by name.
- **No `prompts/get` or `resources/read`.** The SDK wires MRTR into all three methods; humcp calls
  only `tools/call`, and prompts and resources are v2 (§11).
- **No cancel button for a call in flight.** Cancellation is Phase 7, and §3.5 adds a reason for it.
- **No progress reporting while a call waits on a question.** Pitfall 9, Phase 6.
- **No memory of an answer between calls.** MRU history records what was typed into a *tool's*
  parameters (§4, pitfall 3); an elicitation's fields belong to one round trip of one call, and
  keeping what somebody typed into a question a server asked once is a privacy decision nobody has
  asked for.

---

## 7. Additions to the roadmap — proposed

**Contract (§7):**

31. **A field whose value will be sent whether or not it is on screen must be on screen.** Hiding a
    field is only honest where hiding also means omitting. True of a tool form, where an untouched
    optional parameter is not sent; false of an elicitation form, where the client SDK writes every
    schema-stated default back in (§3.3).
32. **A property set on a composite is not a property the part that takes focus reports.** A
    `DatePicker` focuses a text box inside itself; the box inherits the name and the help text by
    peer fallback and inherits neither `IsRequiredForForm` nor the access key, so a required date
    announced itself exactly like an optional one (§4.3). Set them where the focus lands. Where a
    property cannot be got there at all, §7.15 is the fallback: say the keystroke in words.

Two corollaries, stated separately because both were tried and both failed:

- **A live region is announced by its text changing, not by the event the application raises.**
  There is no quiet `SetStatus`, and a message that must not be spoken must not be a status message
  (§4.8).
- **A disabled control is absent from the tab order, not merely inert.** A command with no binding,
  or one whose `CanExecute` has not been re-asked since the value that enables it changed, is a
  button nobody can reach — while its help text goes on telling them to press it (§4.9).

**Testing notes (§9):**

- **A per-connection callback correlates to a per-call context through an `AsyncLocal`**, and the
  test for it has to keep two calls outstanding at once — one after the other passes whether or not
  the correlation exists.
- **The fixture reports the raw wire content of an input response**, not only its deserialised form.
  §3.3 is invisible from either end alone: humcp sends one property and the tool receives four, and
  only the untouched JSON says which side added them.
- **A test that looks a control up by name can find its `Label` instead.** §7.3 wants both to carry
  the name, so the first version of the tab-order assertion here compared the *label*'s position
  with the button's, passed, and proved nothing about the thing it was named for. Filter to
  focusable elements. And for a composite take the *innermost* match: a `DatePicker` and its inner
  text box both answer to the field's name, and only one of them ever has the focus (§4.3).
- **Drive setup through UI Automation patterns; keep keystrokes for the assertions that are about
  the keyboard.** A shared helper that typed made every test that used it fail together whenever
  something else on the desktop held focus — an environment condition arriving as a row of red.
- **A drop-down left open holds its window shut.** FlaUI's `ComboBox.Select` opens the popup to
  choose, and a WPF popup that is still open swallows `Window.Close()` — so the question was never
  answered, the call stayed in flight, and the *next* test failed on a Run button that never
  enabled. Collapse what you expand. The symptom appears one test later than the cause, which is
  the expensive kind.

**Elsewhere:** §3 gains url mode and loses an assumption (§3.2, §3.6); §5 gains the elicitation
exception to the omission rule; §6.5 records where this phase's code went; §10's Phase 5 entry is
done; §10's Phase 6 entry gains announcement policy (§4.4) and Phase 7 a second reason for
cancellation (§3.5); §11's v3 entry gains url-mode elicitation; and §13 can close the question of
where Phase 3's effort should have gone — the date picker was the control the corpus never touched,
and it is where this phase's worst defect was (§4.3).

---

## 8. What is outstanding

- **JAWS and Narrator have still seen none of this**, carried forward from Phase 0 through Phases 3
  and 4.
- **The ten-round cap is the SDK's** (§3.5). humcp has no way to count silent round trips and no way
  out of one but cancellation, which is Phase 7.
- **url mode is declined rather than supported** (§3.6), which will make some real servers
  unusable — none in the corpus, because the corpus is `tools/list` documents and this is a runtime
  behaviour, so the honest statement is that nobody here knows how common it is.
- **A date field's access key is never announced**, only written into its help text (§4.3). The key
  works; a reader will not say which letter it is, and neither setting the property on the box nor
  re-pointing the `Label` changed that. Worth one more attempt with a custom `AutomationPeer` if
  anybody meets it again.
- **No MRTR server outside this repository has been tried.** The exchange is exercised end to end
  against the fixture, and the fixture was written from the SDK's own API rather than from a third
  party's behaviour. Phase 1's lesson was that pointing the client at a real server found a defect
  no fixture would have (§4, pitfall 10); the same is owed here, and there was no public MRTR server
  to point it at at the time of writing.
- **Nothing has been tried with two servers connected at once.** The AsyncLocal correlation is
  tested with two calls on one connection (§3.1), which is the harder case, but the shell that would
  let a user open two servers is Phase 6.
