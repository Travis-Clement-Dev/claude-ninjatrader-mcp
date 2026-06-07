"""
OCM Footprint MCP server — exposes the NinjaTrader order-flow snapshot + analytics to Claude.

Reads the bridge's JSON file-drop (Stage C transport = file; HTTP arrives in Stage D),
computes analytics (imbalances / divergence / POC / value area) via footprint.py, and
serves them as MCP tools. Mirrors the Quantower MCP server's SDK usage.

Transport: stdio (subprocess; JSON-RPC over stdin/stdout).
CRITICAL: stdout is reserved for JSON-RPC. ALL logging goes to stderr.
"""

import asyncio
import base64
import json
import logging
import os
import subprocess
import sys
import tempfile
import urllib.request
from typing import Any

from mcp.server import Server
from mcp.server.stdio import stdio_server
from mcp.types import ImageContent, TextContent, Tool

import footprint as fp

CAPTURE_PS1 = os.path.join(os.path.dirname(os.path.abspath(__file__)), "capture.ps1")

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    stream=sys.stderr,
)
logger = logging.getLogger("ocm-footprint-mcp")

server: Server = Server("ocm-footprint")


def _text(obj: Any) -> list[TextContent]:
    return [TextContent(type="text", text=json.dumps(obj, default=str))]


def _load(instrument=None, period=None):
    path = fp.find_latest_snapshot(instrument=instrument, period=period)
    if not path:
        return None, None
    return path, fp.load_snapshot(path)


@server.list_tools()
async def list_tools() -> list[Tool]:
    inst_period = {
        "instrument": {"type": "string",
                       "description": "Filter by instrument token in the filename (e.g. 'MNQ'). Optional; default = newest snapshot."},
        "period": {"type": "string",
                   "description": "Filter by period token in the filename (e.g. '1Minute'). Optional."},
    }
    return [
        Tool(
            name="get_footprint",
            description=(
                "Full per-bar footprint for the most recent CAPTURED bars: OHLC, direction, "
                "total volume, delta, intra-bar min/max delta, session cumulative delta, POC, "
                "value area, delta-divergence flag, and the complete bid/ask ladder PLUS diagonal "
                "imbalances and stacked runs. Reads the live snapshot the NinjaScript bridge writes "
                "on each bar close. Pre-load bars (empty ladders) are filtered out."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "count": {"type": "integer", "default": 10, "minimum": 1, "maximum": 500,
                              "description": "Number of most-recent captured bars."},
                    **inst_period,
                },
                "required": [], "additionalProperties": False,
            },
        ),
        Tool(
            name="get_orderflow_summary",
            description=(
                "Compact, ladder-free summary of recent order flow — one line per captured bar: "
                "direction, volume, delta, cumulative delta, POC, value area, divergence, and "
                "imbalance counts (buy/sell/stacked). Use this for 'what is order flow doing' "
                "questions without shipping the full ladders."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "count": {"type": "integer", "default": 20, "minimum": 1, "maximum": 500},
                    **inst_period,
                },
                "required": [], "additionalProperties": False,
            },
        ),
        Tool(
            name="get_imbalances",
            description=(
                "Just the diagonal buy/sell imbalances and stacked runs per captured bar "
                "(thresholds: 4:1 diagonal, >=3 consecutive = stacked; tunable in footprint.py). "
                "Use to spot absorption / initiative and stacked imbalance zones."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "count": {"type": "integer", "default": 20, "minimum": 1, "maximum": 500},
                    **inst_period,
                },
                "required": [], "additionalProperties": False,
            },
        ),
        Tool(
            name="list_snapshots",
            description=(
                "List the footprint snapshot files currently available (instrument, period, "
                "captured-bar count, last-modified). Use to see which charts the bridge is feeding."
            ),
            inputSchema={"type": "object", "properties": {}, "required": [], "additionalProperties": False},
        ),
        Tool(
            name="get_screenshot",
            description=(
                "Capture the NinjaTrader chart and return it as a PNG image. PRIMARY path is "
                "bridge-side: the OCM Footprint Bridge indicator renders the chart window via "
                "NinjaTrader's own GetScreenshot over a loopback HTTP endpoint — this is "
                "OCCLUSION-PROOF and Direct2D-correct (works even if the chart is buried behind "
                "other windows or off-screen). Use it to see the live chart/footprint. Optionally "
                "filter by `instrument`/`period` to pick which chart when several are running. "
                "If the bridge HTTP endpoint isn't reachable, falls back to a screen-grab "
                "(`window` substring, e.g. 'Chart' or 'NinjaScript' to read the editor's compile-error "
                "panel) — the screen-grab requires the window to be VISIBLE and is less reliable on "
                "the DirectX chart."
            ),
            inputSchema={
                "type": "object",
                "properties": {
                    "instrument": {"type": "string",
                                   "description": "Instrument token to pick the chart's HTTP port (e.g. 'MNQ'). Optional; default = newest."},
                    "period": {"type": "string",
                               "description": "Period token to pick the chart (e.g. '1Minute'). Optional."},
                    "window": {"type": "string",
                               "description": "Screen-grab fallback only: window-title substring (e.g. 'Chart', 'NinjaScript'). Ignored when the bridge HTTP endpoint is reachable."},
                },
                "required": [], "additionalProperties": False,
            },
        ),
    ]


@server.call_tool()
async def call_tool(name: str, arguments: dict[str, Any]) -> list[TextContent | ImageContent]:
    logger.info("call_tool name=%s arguments=%s", name, arguments)

    if name == "list_snapshots":
        import glob
        out = []
        for f in glob.glob(os.path.join(fp.DEFAULT_SNAPSHOT_DIR, "*.json")):
            if f.endswith("_smokeA.json"):
                continue
            try:
                snap = fp.load_snapshot(f)
                out.append({
                    "file": os.path.basename(f),
                    "instrument": snap.get("instrument"),
                    "barsPeriod": snap.get("barsPeriod"),
                    "capturedBars": len(fp.captured_bars(snap)),
                    "generatedUtc": snap.get("generatedUtc"),
                })
            except Exception as e:  # noqa: BLE001
                out.append({"file": os.path.basename(f), "error": str(e)})
        return _text({"snapshotDir": fp.DEFAULT_SNAPSHOT_DIR, "snapshots": out})

    if name == "get_screenshot":
        instrument = arguments.get("instrument")
        period = arguments.get("period")
        window = (arguments.get("window") or "").strip()

        # PRIMARY: bridge-side capture over loopback HTTP (occlusion-proof, Direct2D-correct).
        port = fp.find_http_port(instrument=instrument, period=period)
        if port:
            try:
                with urllib.request.urlopen(f"http://127.0.0.1:{port}/screenshot", timeout=30) as resp:
                    data = resp.read()
                if data:
                    b64 = base64.b64encode(data).decode("ascii")
                    logger.info("get_screenshot via HTTP port=%d -> %d bytes", port, len(data))
                    return [ImageContent(type="image", data=b64, mimeType="image/png")]
                logger.warning("get_screenshot HTTP port=%d returned empty body; falling back", port)
            except Exception as e:  # noqa: BLE001
                logger.warning("get_screenshot HTTP port=%d failed (%s); falling back to screen-grab", port, e)
        else:
            logger.info("get_screenshot: no bridge HTTP port discovered; using screen-grab fallback")

        # FALLBACK: screen-grab (requires the window visible; less reliable on DirectX chart).
        out = os.path.join(tempfile.gettempdir(), "ocm_capture.png")
        cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass",
               "-File", CAPTURE_PS1, "-OutPath", out]
        if window:
            cmd += ["-WindowTitle", window]
        try:
            subprocess.run(cmd, capture_output=True, timeout=30)
            with open(out, "rb") as fh:
                data = fh.read()
        except Exception as e:  # noqa: BLE001
            return _text({"error": f"screenshot failed: bridge HTTP unavailable (port={port}) "
                                   f"and screen-grab errored: {e}. Is the OCM Footprint Bridge "
                                   f"indicator on a chart, or is NinjaTrader visible?"})
        b64 = base64.b64encode(data).decode("ascii")
        logger.info("get_screenshot via screen-grab window=%r -> %d bytes", window, len(data))
        return [ImageContent(type="image", data=b64, mimeType="image/png")]

    instrument = arguments.get("instrument")
    period = arguments.get("period")
    path, snap = _load(instrument, period)
    if snap is None:
        return _text({"error": "No footprint snapshot found. Is the 'OCM Footprint Bridge' "
                               "indicator attached to a chart in NinjaTrader (and a bar closed yet)?",
                      "snapshotDir": fp.DEFAULT_SNAPSHOT_DIR})

    count = int(arguments.get("count", 10))

    if name == "get_footprint":
        return _text(fp.analyze_snapshot(snap, count=count))

    if name == "get_orderflow_summary":
        full = fp.analyze_snapshot(snap, count=count)
        keep = ("timeUtc", "direction", "totalVolume", "delta", "cumulativeDelta",
                "poc", "valueAreaHigh", "valueAreaLow", "deltaDivergence")
        bars = []
        for b in full["bars"]:
            row = {k: b[k] for k in keep}
            row["buyImb"] = len(b["imbalances"]["buy"])
            row["sellImb"] = len(b["imbalances"]["sell"])
            row["stacked"] = b["imbalances"]["stacked"]
            bars.append(row)
        meta = {k: full[k] for k in ("instrument", "barsPeriod", "generatedUtc", "capturedBars", "rangeRemaining")}
        return _text({**meta, "bars": bars})

    if name == "get_imbalances":
        full = fp.analyze_snapshot(snap, count=count)
        return _text({
            "instrument": full["instrument"],
            "barsPeriod": full["barsPeriod"],
            "bars": [{"timeUtc": b["timeUtc"], "direction": b["direction"],
                      "imbalances": b["imbalances"]} for b in full["bars"]],
        })

    return _text({"error": f"Unknown tool: {name}"})


async def main() -> None:
    logger.info("ocm-footprint MCP server starting; snapshotDir=%s; "
                "tools=get_footprint,get_orderflow_summary,get_imbalances,list_snapshots",
                fp.DEFAULT_SNAPSHOT_DIR)
    async with stdio_server() as (read_stream, write_stream):
        await server.run(read_stream, write_stream, server.create_initialization_options())


if __name__ == "__main__":
    asyncio.run(main())
