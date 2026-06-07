"""
OCM Footprint analytics — pure stdlib, NO MCP dependency (so it's testable standalone).

Loads the bridge's JSON snapshot, filters to *captured* bars (non-empty ladder),
and computes order-flow analytics: diagonal imbalances (+ stacked runs), delta
divergence, POC, and value area. Thresholds default to the user's TDU settings and
are tunable here WITHOUT recompiling NinjaScript.

Smoke-test against the latest snapshot:
    py footprint.py
"""
from __future__ import annotations

import glob
import json
import os
import sys
from typing import Optional

DEFAULT_SNAPSHOT_DIR = os.environ.get(
    "OCM_SNAPSHOT_DIR",
    os.path.join(os.path.expanduser("~"), "Documents", "NinjaTrader 8", "OCM", "footprint_snapshots"),
)

# Analytics defaults (from the user's TDU template; tune here, no NinjaScript recompile).
IMBALANCE_RATIO = 4.0       # ask(P) >= ratio * bid(P-1 level) => diagonal BUY imbalance
MIN_IMBALANCE_VOLUME = 0    # ignore levels whose aggressor volume is below this
STACK_COUNT = 3             # >= N consecutive same-side imbalanced levels => stacked
VALUE_AREA_PCT = 0.70       # value area = this fraction of bar volume around POC


# ── snapshot loading ─────────────────────────────────────────────────────────
def find_latest_snapshot(dir_path: str = DEFAULT_SNAPSHOT_DIR,
                         instrument: Optional[str] = None,
                         period: Optional[str] = None) -> Optional[str]:
    """Newest canonical snapshot file (excludes *_smokeA.json), optionally filtered."""
    files = [f for f in glob.glob(os.path.join(dir_path, "*.json"))
             if not f.endswith("_smokeA.json")]
    if instrument:
        token = instrument.replace(" ", "_")
        files = [f for f in files if token in os.path.basename(f)]
    if period:
        files = [f for f in files if period in os.path.basename(f)]
    return max(files, key=os.path.getmtime) if files else None


def load_snapshot(path: str) -> dict:
    with open(path, "r", encoding="utf-8") as fh:
        return json.load(fh)


def find_http_port(dir_path: str = DEFAULT_SNAPSHOT_DIR,
                   instrument: Optional[str] = None,
                   period: Optional[str] = None) -> Optional[int]:
    """Discover the bridge's loopback HTTP port (Stage B/C).

    Prefer the latest snapshot's ``httpPort`` field; fall back to a
    ``<instr>_<period>.port`` sidecar file written by the indicator on listener start.
    Returns None if no port can be found (caller then falls back to screen-grab).
    """
    path = find_latest_snapshot(dir_path, instrument, period)
    if path:
        try:
            p = load_snapshot(path).get("httpPort")
            if isinstance(p, int) and p > 0:
                return p
        except Exception:
            pass
    files = glob.glob(os.path.join(dir_path, "*.port"))
    if instrument:
        token = instrument.replace(" ", "_")
        files = [f for f in files if token in os.path.basename(f)]
    if period:
        files = [f for f in files if period in os.path.basename(f)]
    if files:
        try:
            with open(max(files, key=os.path.getmtime), encoding="utf-8") as fh:
                return int(fh.read().strip())
        except Exception:
            pass
    return None


def captured_bars(snapshot: dict) -> list[dict]:
    """Only bars that actually captured order flow (non-empty ladder).

    Pre-load / historical bars come through with an empty ladder because
    OnMarketData is realtime-only; we filter them so Claude never sees them.
    """
    return [b for b in snapshot.get("bars", []) if b.get("ladder")]


# ── analytics ────────────────────────────────────────────────────────────────
def _ladder_index_map(bar: dict, tick: float) -> dict[int, dict]:
    """tick-index -> {'bid','ask','vol'} for O(1) diagonal-neighbor lookups."""
    out: dict[int, dict] = {}
    for row in bar.get("ladder", []):
        idx = round(row["price"] / tick)
        bid, ask = int(row["bid"]), int(row["ask"])
        out[idx] = {"bid": bid, "ask": ask, "vol": bid + ask}
    return out


def poc(bar: dict):
    """Point of control: price with the highest total (bid+ask) volume."""
    best_price, best_vol = None, -1
    for row in bar.get("ladder", []):
        v = int(row["bid"]) + int(row["ask"])
        if v > best_vol:
            best_vol, best_price = v, row["price"]
    return best_price


def value_area(bar: dict, tick: float, pct: float = VALUE_AREA_PCT):
    """(value_area_high, value_area_low): contiguous band around POC holding `pct` of volume."""
    vol = {idx: c["vol"] for idx, c in _ladder_index_map(bar, tick).items()}
    total = sum(vol.values())
    if not vol or total == 0:
        return (None, None)
    target = pct * total
    lo = hi = max(vol, key=lambda k: vol[k])  # start at POC
    acc = vol[hi]
    kmin, kmax = min(vol), max(vol)
    while acc < target and (lo > kmin or hi < kmax):
        up_v = vol.get(hi + 1, 0) if hi < kmax else -1
        dn_v = vol.get(lo - 1, 0) if lo > kmin else -1
        if up_v >= dn_v:
            hi += 1
            acc += max(up_v, 0)
        else:
            lo -= 1
            acc += max(dn_v, 0)
    return (round(hi * tick, 10), round(lo * tick, 10))


def delta_divergence(bar: dict) -> Optional[str]:
    """Candle-color-vs-delta divergence (TDU 'Candle_Color_vs_Delta')."""
    o, c, d = bar["open"], bar["close"], bar["delta"]
    if c > o and d < 0:
        return "bearish"   # up candle on negative delta
    if c < o and d > 0:
        return "bullish"   # down candle on positive delta
    return None


def imbalances(bar: dict, tick: float,
               ratio: float = IMBALANCE_RATIO,
               min_vol: int = MIN_IMBALANCE_VOLUME,
               stack: int = STACK_COUNT) -> dict:
    """Diagonal buy/sell imbalances per price level, plus stacked runs.

    Diagonal: BUY at P if ask(P) >= ratio * bid(P-1 tick); SELL at P if
    bid(P) >= ratio * ask(P+1 tick). A zero diagonal neighbor with aggressor
    volume present is treated as an imbalance (infinite ratio).
    """
    m = _ladder_index_map(bar, tick)
    if not m:
        return {"buy": [], "sell": [], "stacked": []}
    lo, hi = min(m), max(m)
    buy, sell = [], []
    for i in range(lo, hi + 1):
        cell = m.get(i, {"bid": 0, "ask": 0})
        ask, bid = cell["ask"], cell["bid"]
        lower_bid = m.get(i - 1, {"bid": 0})["bid"]
        upper_ask = m.get(i + 1, {"ask": 0})["ask"]
        if ask > 0 and ask >= min_vol and (ask >= ratio * lower_bid if lower_bid > 0 else True):
            buy.append(round(i * tick, 10))
        if bid > 0 and bid >= min_vol and (bid >= ratio * upper_ask if upper_ask > 0 else True):
            sell.append(round(i * tick, 10))
    stacked = (_stacked_runs(buy, "buy", tick, stack)
               + _stacked_runs(sell, "sell", tick, stack))
    return {"buy": buy, "sell": sell, "stacked": stacked}


def _stacked_runs(prices: list, side: str, tick: float, stack: int) -> list[dict]:
    """Runs of >= `stack` consecutive tick levels on the same side."""
    if not prices:
        return []
    idxs = sorted(round(p / tick) for p in prices)
    runs, start, prev = [], idxs[0], idxs[0]
    for i in idxs[1:]:
        if i == prev + 1:
            prev = i
            continue
        if prev - start + 1 >= stack:
            runs.append({"side": side, "from": round(start * tick, 10),
                         "to": round(prev * tick, 10), "count": prev - start + 1})
        start = prev = i
    if prev - start + 1 >= stack:
        runs.append({"side": side, "from": round(start * tick, 10),
                     "to": round(prev * tick, 10), "count": prev - start + 1})
    return runs


def analyze_bar(bar: dict, tick: float, **kw) -> dict:
    """Bar's raw fields plus computed analytics."""
    vah, val = value_area(bar, tick, kw.get("value_area_pct", VALUE_AREA_PCT))
    imb = imbalances(bar, tick,
                     ratio=kw.get("ratio", IMBALANCE_RATIO),
                     min_vol=kw.get("min_vol", MIN_IMBALANCE_VOLUME),
                     stack=kw.get("stack", STACK_COUNT))
    return {
        "index": bar.get("index"),
        "timeUtc": bar.get("timeUtc"),
        "open": bar["open"], "high": bar["high"], "low": bar["low"], "close": bar["close"],
        "direction": bar.get("direction"),
        "totalVolume": bar.get("totalVolume"),
        "delta": bar.get("delta"),
        "minDelta": bar.get("minDelta"), "maxDelta": bar.get("maxDelta"),
        "cumulativeDelta": bar.get("cumulativeDelta"),
        "poc": poc(bar),
        "valueAreaHigh": vah, "valueAreaLow": val,
        "deltaDivergence": delta_divergence(bar),
        "imbalances": imb,
    }


def analyze_snapshot(snapshot: dict, count: Optional[int] = None, **kw) -> dict:
    """Filter to captured bars, analyze each, return a Claude-friendly envelope."""
    tick = float(snapshot.get("tickSize", 0.25))
    bars = captured_bars(snapshot)
    if count:
        bars = bars[-count:]
    return {
        "schema": "ocm-footprint-analytics/v1",
        "instrument": snapshot.get("instrument"),
        "barsPeriod": snapshot.get("barsPeriod"),
        "tickSize": tick,
        "generatedUtc": snapshot.get("generatedUtc"),
        "rangeRemaining": snapshot.get("rangeRemaining"),
        "capturedBars": len(captured_bars(snapshot)),
        "bars": [analyze_bar(b, tick, **kw) for b in bars],
    }


# ── standalone smoke test ────────────────────────────────────────────────────
if __name__ == "__main__":
    try:
        sys.stdout.reconfigure(encoding="utf-8")  # Windows console defaults to cp1252
    except Exception:
        pass
    path = find_latest_snapshot()
    if not path:
        print("No snapshot found in", DEFAULT_SNAPSHOT_DIR)
        raise SystemExit(1)
    snap = load_snapshot(path)
    result = analyze_snapshot(snap, count=6)
    print(f"snapshot : {os.path.basename(path)}")
    print(f"instrument {result['instrument']}  period {result['barsPeriod']}  "
          f"tick {result['tickSize']}  capturedBars {result['capturedBars']}")
    print("-" * 96)
    for b in result["bars"]:
        imb = b["imbalances"]
        print(f"{b['timeUtc'][11:19]}  {b['direction']:<4}  vol={b['totalVolume']:<5} "
              f"Δ={b['delta']:<5} cumΔ={b['cumulativeDelta']:<6} "
              f"POC={b['poc']}  VA=[{b['valueAreaLow']},{b['valueAreaHigh']}]  "
              f"div={b['deltaDivergence'] or '-':<7} "
              f"imb(buy={len(imb['buy'])},sell={len(imb['sell'])},stacked={len(imb['stacked'])})")
        for s in imb["stacked"]:
            print(f"        STACKED {s['side']} x{s['count']}  {s['from']} -> {s['to']}")
