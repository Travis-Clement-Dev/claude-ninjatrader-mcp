# claude-ninjatrader-mcp

**A read-only order-flow / footprint bridge for NinjaTrader 8, served to Claude (or any MCP client) over the Model Context Protocol — plus occlusion-proof chart screenshots. No Order Flow+ / Volumetric license required.**

A NinjaScript indicator reconstructs bid/ask-at-price from `OnMarketData` (the same "BidAsk" method order-flow tools use on a plain chart), writes an atomic JSON snapshot, and a small Python MCP server computes analytics (imbalances, stacked runs, delta divergence, POC, value area) and exposes them to your LLM as tools. **A footprint is data, not a picture** — so Claude can *read and reason about* live order flow.

> ⚠️ **Read-only market data. No account access, no order placement.** Developed and tested on a NinjaTrader **Sim** account. See [Safety](#safety).

---

## Why this exists

Most "NinjaTrader + AI" projects are *general* control servers (place orders, read positions). This one is deliberately narrow and safe:

- **Order flow, specifically** — the bid/ask ladder + delta + imbalances that order-flow traders actually use.
- **No Volumetric license needed** — it reconstructs the ladder from the raw tick stream, so it works on a plain chart.
- **Read-only** — it never touches your account or sends orders. Safe to run while you trade.
- **Data, not pixels** — Claude consumes structured JSON it can reason over, not a screenshot it has to interpret (though it can grab a screenshot too).

## Features

- **Per-bar footprint** — bid/ask volume at every price level, total volume, delta, intra-bar min/max delta, session cumulative delta, OHLC.
- **Analytics (tunable, no recompile)** — diagonal **imbalances** (default 4:1), **stacked** runs (≥3), **delta divergence** (candle-vs-delta), **POC**, **value area** (70%). All thresholds live in `mcp-server/footprint.py`.
- **Occlusion-proof chart vision** — `get_screenshot` renders the chart via NinjaTrader's own engine, so it works even when the window is buried behind others (a plain screen-grab returns black on NT's DirectX chart).
- **Two transports** — atomic JSON file-drop **and** a loopback HTTP endpoint (`127.0.0.1:5670–5679`).
- **100% volume reconciliation** — every bar's ladder sums exactly to the bar's volume (a conservation check that proves capture integrity).

## Architecture

```
NinjaTrader 8 chart (Tick Replay ON)
        │  OnMarketData  → reconstruct bid/ask-at-price (no Volumetric)
        ▼
OCM Footprint Bridge  (NinjaScript indicator)
        │  atomic JSON snapshot  +  loopback HTTP  (/footprint, /screenshot, /health)
        ▼
Python MCP server  (stdio)  → computes analytics, exposes tools
        ▼
Claude / any MCP client
```

## Requirements

- **NinjaTrader 8** (free Sim is fine). Enable **Tick Replay** on the data series for historical footprint accuracy (live-only works without it, but no history).
- **Python 3.10+**.
- An **MCP client** (e.g. Claude Code or Claude Desktop).

## Install — step by step

**1. Install the NinjaScript indicator**
- Copy `OCMFootprintBridge/OCMFootprintBridge.cs` → `Documents\NinjaTrader 8\bin\Custom\Indicators\`.
- In NinjaTrader: **New → NinjaScript Editor → press F5** to compile. *(Ship the class only — NinjaTrader generates its own wrapper region on compile.)*

**2. Add it to a chart**
- Open a chart (e.g. MNQ, 1-minute), **Indicators → "OCM Footprint Bridge" → add**.
- Recommended: turn **Tick Replay ON** for the series. The indicator runs `Calculate.OnEachTick` (read-only).
- It writes snapshots to `Documents\NinjaTrader 8\OCM\footprint_snapshots\` by default (configurable in the indicator's properties).

**3. Set up the Python MCP server**
```bash
cd mcp-server
python -m venv .venv
# Windows:
.venv\Scripts\activate
pip install -r requirements.txt
```

**4. Register the server with your MCP client**
- Copy `.mcp.json.example` → your client's MCP config (e.g. project `.mcp.json`), and edit the absolute paths to point at your `.venv` Python and `server.py`.

**5. Test it**
- Restart your MCP client so it loads the server, then ask it to call **`list_snapshots`** (confirms the bridge is feeding a chart) and **`get_footprint`** (returns the live ladder + analytics).

## MCP tools

| Tool | What it returns |
| --- | --- |
| `get_footprint(count)` | Full per-bar footprint: OHLC, delta/min/max/cum, the bid/ask ladder, POC, value area, divergence flag, and diagonal + stacked imbalances. |
| `get_orderflow_summary(count)` | Compact one-line-per-bar summary (direction, volume, delta, cumΔ, POC, VA, divergence, imbalance counts). |
| `get_imbalances(count)` | Diagonal buy/sell imbalances + stacked runs per bar. |
| `list_snapshots()` | Which charts the bridge is currently feeding (instrument, period, captured-bar count). |
| `get_screenshot(instrument, period)` | Occlusion-proof PNG of the chart window, rendered by NinjaTrader. |

Optional filters `instrument` / `period` pick a specific chart when several are running.

### Example — `get_orderflow_summary`
```jsonc
{
  "instrument": "MNQ 06-26", "barsPeriod": "1 Minute", "capturedBars": 30,
  "bars": [
    { "timeUtc": "…T13:48:00Z", "direction": "down", "totalVolume": 2187,
      "delta": -1060, "cumulativeDelta": -1060, "poc": 30560.5,
      "valueAreaHigh": 30566.0, "valueAreaLow": 30551.25,
      "deltaDivergence": null, "buyImb": 3, "sellImb": 57, "stacked": 2 }
  ]
}
```

## Configuration

- **Analytics thresholds** — edit `mcp-server/footprint.py` (`IMBALANCE_RATIO`, `STACK_COUNT`, `VALUE_AREA_PCT`). No NinjaScript recompile needed.
- **Snapshot / screenshot folders** — indicator properties (default under `Documents\NinjaTrader 8\OCM\`). The MCP server honors the `OCM_SNAPSHOT_DIR` environment variable.
- **HTTP ports** — `5670–5679` (first free), loopback only.

## Safety

- The indicator subscribes to **market data only** — it has **no account access and never places, modifies, or cancels orders**.
- The HTTP endpoint binds to **127.0.0.1 (loopback)** only.
- Developed/tested on a **Sim** account. Use at your own risk; markets carry risk of loss.

## How it works

On each `Last` trade, the bridge classifies the aggressor (`price ≥ bestAsk` → buy/ask volume; `price ≤ bestBid` → sell/bid volume; else tick-rule fallback) and accumulates a per-price `bid/ask` ladder per bar — the standard "BidAsk" reconstruction, so **no exchange Volumetric feed is required**. On bar close it writes an atomic JSON snapshot; the Python server reads it and layers analytics on top.

## Developer tools (optional)

- `tools/compile_check.ps1` — headless Roslyn compile of the NinjaScript against the live NT assemblies, to catch C# errors before installing. Set `-MsBuild` to your MSBuild path (any VS edition, or via `vswhere`).
- The bridge exposes a `GET /recompile` endpoint that invokes NinjaTrader's own compiler from code (handy for iteration). **Honest limit:** it *compiles* without a manual F5, but **loading** newly-compiled code into running charts still requires an F5 — NinjaTrader binds the hot-reload to its editor.

## License

[MIT](LICENSE) © 2026 Travis Clement.

## Disclaimer

Not affiliated with or endorsed by NinjaTrader, LLC or TradeDevils. "NinjaTrader" is a trademark of its respective owner. This software is for research/educational use; nothing here is financial advice. Trading futures involves substantial risk of loss.
