"""
End-to-end MCP smoke test: spawn server.py over stdio, do the MCP handshake,
list tools, and call get_orderflow_summary against the live snapshot.

    .venv\\Scripts\\python.exe test_client.py
"""
import asyncio
import os
import sys

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client

SERVER = os.path.join(os.path.dirname(os.path.abspath(__file__)), "server.py")


async def main() -> None:
    params = StdioServerParameters(command=sys.executable, args=[SERVER])
    async with stdio_client(params) as (read, write):
        async with ClientSession(read, write) as session:
            await session.initialize()

            tools = await session.list_tools()
            print("TOOLS:", ", ".join(t.name for t in tools.tools))

            res = await session.call_tool("list_snapshots", {})
            print("\nlist_snapshots ->")
            print(res.content[0].text)

            res = await session.call_tool("get_orderflow_summary", {"count": 4})
            print("\nget_orderflow_summary(count=4) ->")
            print(res.content[0].text)

            res = await session.call_tool("get_screenshot", {"window": "Chart"})
            c = res.content[0]
            print(f"\nget_screenshot(window='Chart') -> type={c.type} "
                  f"mime={getattr(c, 'mimeType', None)} "
                  f"b64chars={len(getattr(c, 'data', '') or getattr(c, 'text', ''))}")


if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass
    asyncio.run(main())
