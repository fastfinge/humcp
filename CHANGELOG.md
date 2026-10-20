# Changelog

humcp follows [semantic versioning](https://semver.org). 1.x releases are bug fixes, most of them
from user reports; 2.0 and 3.0 follow the roadmap in `docs/ROADMAP.md`.

## 1.1.0 — unreleased

A fix, released as a minor version because results look different afterwards.

Most MCP servers send their answer as JSON written into a text part, not as structured content.
1.0 showed that text as it arrived. For a weather forecast that is one line of several thousand
characters, with 48 hours in one list and 48 temperatures in another, and nothing to say which goes
with which. You could read it on screen with effort, but not with a screen reader, and humcp exists
for screen reader users. So this is treated as a defect rather than a missing feature, and fixed in
1.x rather than held for 2.0.

- Text that is entirely JSON is shown the way structured content is: as a tree, or as a table with
  column headers. A title line or two in front of the JSON becomes its heading. Text that is only
  partly JSON, or is not JSON, is shown as text, exactly as before. F1 and Copy still give the
  text exactly as the server sent it.
- A set of equal-length lists, such as hours, temperatures and rainfall, is shown as one table with
  a row for each hour, wherever it appears in a result.
- A server that sends structured content and the same thing again as JSON text, as the MCP
  specification asks, is shown once rather than twice.

Also fixed:

- An entry in a list can be changed without removing it and typing it again. The obvious place this
  was missing was a server's environment variables: you could change everything else about a server,
  but to change a variable's value you had to remove it and add it again. Select an entry and press
  F2, or the new Edit button. It opens in a small window of its own, and Enter closes it with the
  change kept. This works for every list of values in every form, not just environment variables.
- Closing a humcp window, such as a form or a server's settings, no longer sometimes moves you into
  whatever application was in front before humcp. You now return to the humcp window it was opened
  from.

## 1.0.0 — 2026-10-19

The first release.

- A tree of configured servers and their tools, each server's state, tool count and protocol
  version in its name; first-letter navigation; prefix groups on large servers.
- A generated, screen-reader-first form for every tool, from its JSON Schema: native controls for
  95% of real-world parameters, a JSON editor with a stated reason for the rest, and a form for every
  tool however unusual its schema.
- Client-side validation that advises and never blocks, history for typed values, optional fields
  hidden until asked for.
- Results rendered by shape: text, sortable tables, structure trees, images, audio, files and links,
  each with a control of its own.
- Questions servers ask mid-call (MCP's multi round-trip requests and elicitation), as forms.
- Servers over stdio and over unauthenticated Streamable HTTP; MCP 2026-07-28, negotiating back to
  older servers.
- Servers added, edited, imported from other clients' configuration, reconnected and removed from
  the keyboard; settings; tool lists that update when a server changes them.
- Progress reporting, stopping a running tool with Escape, and a confirmation before running a tool
  that says it is destructive.
- A server that stops is reported; every server humcp started ends when humcp does.
- Announcements through the screen reader when humcp is in the background.
- Windows and panes sized to the screen and their content.
- A user guide inside the app.
