# Throwaway: calls real MCP servers and keeps what they returned, exactly. Spike material for
# ROADMAP §11.1, under the corpus rule: nobody here chose what is in it.
import asyncio, base64, json, sys, datetime
from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

OUT = sys.argv[1]
RUNS = json.load(open(sys.argv[2]))

async def run(server):
    params = StdioServerParameters(command=server["command"], args=server["args"])
    async with stdio_client(params) as (r, w):
        async with ClientSession(r, w) as s:
            await s.initialize()
            for call in server["calls"]:
                res = await s.call_tool(call["tool"], call["arguments"])
                dump = res.model_dump(mode="json", by_alias=True, exclude_none=True)
                name = f'{server["id"]}-{call["name"]}'
                rec = {"server": server["id"], "started": [server["command"], *server["args"]],
                       "tool": call["tool"], "arguments": call["arguments"],
                       "captured": datetime.date.today().isoformat(), "result": dump}
                json.dump(rec, open(f"{OUT}/results/{name}.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)
                for i, c in enumerate(dump.get("content", [])):
                    if c.get("type") == "image":
                        ext = {"image/png": "png", "image/jpeg": "jpg", "image/webp": "webp"}.get(c["mimeType"], "bin")
                        open(f"{OUT}/images/{name}-{i}.{ext}", "wb").write(base64.b64decode(c["data"]))
                sizes = [len(c.get("text", c.get("data", ""))) for c in dump.get("content", [])]
                print(name, "error" if dump.get("isError") else "ok", sizes, flush=True)

async def main():
    for server in RUNS:
        try:
            await run(server)
        except Exception as e:
            print(server["id"], "FAILED", repr(e), flush=True)

asyncio.run(main())
