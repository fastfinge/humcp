# Image fixtures

Images real MCP servers returned, kept as they arrived, under the corpus rule: captured, never drawn
for the test. They are what the local model is asked to describe (ROADMAP §11, Phase 10).

| File | Captured | From |
|---|---|---|
| `weather-gc-ca.png` | 2026-10-20, for the v2 LLM spike (`docs/v2-llm-spike.md` §7) | `@playwright/mcp`, headless, `browser_take_screenshot` of a Government of Canada weather forecast page (weather.gc.ca). Government of Canada content, reproduced under the Open Government Licence – Canada. |

`Humcp.Inference.Tests` describes it only when a model has been downloaded (`HUMCP_MODELS`).
