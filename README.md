# humcp

An accessible, screen-reader-first Windows client for
[Model Context Protocol](https://modelcontextprotocol.io) servers.

MCP servers publish machine-readable descriptions of their tools: names, purposes, and the exact
shape of what each one needs. humcp turns those descriptions into keyboard-first, screen-reader-ready
forms and results, automatically, for any server. There is no per-server code, and no AI in the way:
you run the tools yourself.

- A tree of your servers and their tools, with status, tool counts and errors in each name.
- A generated form for every tool: real labels, help text, required fields, access keys, validation
  that names the problem and moves you to it, and history for what you typed before.
- Results as controls that suit them: text you can review line by line, tables as grids with column
  headers that you can sort, structures as trees, pictures and files you can save.
- Questions a server asks mid-call, as forms of their own.
- Servers you start (stdio) and servers on the web (Streamable HTTP, without sign-in for now).
- Paste your configuration from Claude Desktop, VS Code or any other client to bring your servers
  across.

Built and tested with NVDA. Every form, result and window is checked against the live UI Automation
tree, and every release is walked through by ear.

## Download

Get the latest release from the
[Releases page](https://github.com/fastfinge/humcp/releases/latest):

- `humcp-x64.msi`, a Windows Installer package that installs for you only and needs no
  administrator rights; or
- `humcp-win-x64.zip` / `humcp-win-arm64.zip`, portable: extract and run `Humcp.App.exe`.

Windows 10 or 11. Releases are not code-signed yet, so SmartScreen may warn: choose More info, then
Run anyway. Each release lists SHA-256 checksums.

## Documentation

- [User guide](docs/user-guide.md), also in the app under Help, User guide.
- [Changelog](CHANGELOG.md).
- [Roadmap](docs/ROADMAP.md): the design, and what versions 2 and 3 will bring.

## Building from source

Requires the .NET 10 SDK on Windows.

```
dotnet build
dotnet test tests/Humcp.Schema.Tests
dotnet test tests/Humcp.Core.Tests
dotnet run --project src/Humcp.App
```

`tests/Humcp.App.UiTests` drives the real application with real keystrokes through UI Automation,
so it takes over the desktop while it runs; it is run before each release rather than in CI.
`CLAUDE.md` and `docs/ROADMAP.md` describe the architecture and the accessibility contract every
change is held to.

## Reporting problems

[Open an issue](https://github.com/fastfinge/humcp/issues/new/choose). Bug fixes ship as 1.x
releases. Security problems: see [SECURITY.md](SECURITY.md).

## Licence

humcp is released under the [BSD Zero Clause License](LICENSE): do what you like with it. The
components it ships with have their own licences, listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
