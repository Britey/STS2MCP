"""Independent seat mailboxes backed by persistent authenticated MCP sessions."""
import argparse
import asyncio
import json
import os
from pathlib import Path
import sys
from time import time_ns

from mcp import ClientSession, StdioServerParameters
from mcp.client.stdio import stdio_client


def list_tools(catalog, prefix):
    return {name: {"description": (tool.description or name).splitlines()[0],
                   "inputSchema": tool.inputSchema}
            for name, tool in catalog.items() if name.startswith(prefix)}


def now():
    return time_ns() // 1_000_000


def write(path, data):
    temp = path.with_suffix(".writing")
    temp.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
    temp.replace(path)


def trace(log, **event):
    log.write(json.dumps(event, ensure_ascii=False) + "\n")
    log.flush()


def payload(result):
    raw = "\n".join(getattr(part, "text", "") for part in result.content)
    try:
        return json.loads(raw)
    except ValueError:
        return raw


async def relay(root, seat, port, server, gate, arrived, seat_count):
    inbox = root / seat
    inbox.mkdir(exist_ok=True)
    token = root / "profiles" / seat / "SlayTheSpire2" / "STS2_MCP.token"
    config = StdioServerParameters(
        command=sys.executable,
        args=[server, "--host", "127.0.0.1", "--port", str(port),
              "--token-file", str(token), "--no-trust-env"],
        env=dict(os.environ, PYTHONUTF8="1", PYTHONDONTWRITEBYTECODE="1"),
    )
    with (root / f"{seat}-mcp.jsonl").open("w", encoding="utf-8") as log:
        start = now()
        async with stdio_client(config) as (reader, writer), ClientSession(reader, writer) as mcp:
            info = await mcp.initialize()
            initialized = now()
            tools = (await mcp.list_tools()).tools
            discovered = now()
            catalog = {tool.name: tool for tool in tools}
            trace(log, kind="startup", seat=seat, start=start, initialized=initialized,
                  discovered=discovered, protocol=info.protocolVersion, tool_count=len(tools))
            write(inbox / "ready.json", {"start": start, "initialized": initialized,
                                         "discovered": discovered, "tool_count": len(tools)})
            last_cursor = 0
            while not (root / "stop-relay").exists():
                requests = sorted(inbox.glob("*.request"), key=lambda p: p.stat().st_mtime_ns)
                for path in requests:
                    request = json.loads(path.read_text(encoding="utf-8-sig"))
                    if not request["observer"] and not gate.is_set():
                        arrived.setdefault(seat, {"id": path.stem, "requested": request["requested"], "seen": now()})
                        if len(arrived) == seat_count:
                            write(root / "release.json", {"release": now(), "first_requests": arrived})
                            gate.set()
                        else:
                            continue
                    name, args = request["name"], request["args"]
                    if not request["observer"] and args.get("wait_seconds"):
                        if name == "mp_coordination_state":
                            args.setdefault("after_id", last_cursor)
                        elif name == "mp_combat_decision":
                            args.setdefault("coordination_after_id", last_cursor)
                    begin = now()
                    error = False
                    try:
                        if name == "list":
                            value = list_tools(catalog, args.get("prefix", "mp_"))
                        elif name == "describe":
                            tool = catalog[args["name"]]
                            value = {"description": tool.description, "inputSchema": tool.inputSchema}
                        else:
                            result = await mcp.call_tool(name, args)
                            error, value = bool(result.isError), payload(result)
                    except Exception as exc:
                        error, value = True, f"{type(exc).__name__}: {exc}"
                    if not request["observer"] and isinstance(value, dict):
                        snapshot = value.get("state", value)
                        chat = value if name == "mp_coordination_state" else snapshot.get("coordination") if isinstance(snapshot, dict) else None
                        if isinstance(chat, dict) and isinstance(chat.get("latest_id"), int):
                            last_cursor = chat["latest_id"]
                    returned = now()
                    write(path.with_suffix(".response"), {"error": error, "result": value})
                    trace(log, kind="request", id=path.stem, seat=seat, observer=request["observer"],
                          requested=request["requested"], submitted=begin, returned=returned, written=now(),
                          name=name, args=args, error=error, result=value)
                    path.unlink()
                await asyncio.sleep(.02)


async def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("root", type=Path)
    parser.add_argument("server", type=Path)
    parser.add_argument("--seats", type=int, choices=(2, 3), default=2)
    parser.add_argument("--first-port", type=int, default=15626)
    args = parser.parse_args()
    gate = asyncio.Event()
    arrived = {}
    seats = ("host", "guest", "guest2")[:args.seats]
    await asyncio.gather(*(relay(args.root, seat, args.first_port + i, str(args.server.resolve()),
                                 gate, arrived, len(seats)) for i, seat in enumerate(seats)))


if __name__ == "__main__":
    asyncio.run(main())
