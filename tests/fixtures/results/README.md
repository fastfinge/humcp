# Result fixtures

Real `tools/call` results from published MCP servers, captured on 2026-10-19 for humcp 1.1 and kept
exactly as they arrived, `result` member and all. The same rule as the schema corpus: nobody here
chose what is in them. Do not edit one by hand; capture it again.

They exist because 1.0 rendered a tool's JSON as one line of text when the tool sent it as text
rather than as structured content, and almost every server does. Open-Meteo's tools send bare JSON;
the MLB server sends a title line and then indented JSON for some tools and formatted prose for
others. The prose ones are here on purpose, as the cases that must stay text.

The U+FFFD characters in the MLB player and team names were in the server's reply.

| Server | Package | Started as |
|---|---|---|
| open-meteo | `open-meteo-mcp-server` | `npx -y -p open-meteo-mcp-server open-meteo-mcp-server` |
| mlb | `@jerrysv/mlb-mcp-server` | `npx @jerrysv/mlb-mcp-server bluejays` |
