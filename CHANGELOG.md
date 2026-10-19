# Changelog

humcp follows [semantic versioning](https://semver.org). 1.x releases are bug fixes, most of them
from user reports; 2.0 and 3.0 follow the roadmap in `docs/ROADMAP.md`.

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
