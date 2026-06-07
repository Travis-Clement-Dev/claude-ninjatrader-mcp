//
// OCM Footprint Bridge — Stage B (full capture layer)
//
// Reconstructs order flow (bid/ask-at-price) from live OnMarketData — NO native
// Volumetric license needed (proven in Stage A: 100% volume reconciliation) — and
// emits a rolling snapshot of the last N closed bars as atomic JSON for the Python
// MCP server (Stage C) to consume.
//
// Per bar: OHLC, direction, total volume, delta, intra-bar min/max delta, session
// cumulative delta, and the full bid/ask ladder.
//
// READ-ONLY market data. No account access, no orders. (Developed on Sim101.)
//
#region Using declarations
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// This namespace holds indicators in this folder and is required. Do not change it.
namespace NinjaTrader.NinjaScript.Indicators
{
	/// <summary>
	/// OCM Footprint Bridge — captures order flow from OnMarketData and writes a rolling
	/// last-N-bars JSON snapshot (schema ocm-footprint/v1). Read-only market data.
	/// </summary>
	public class OCMFootprintBridge : Indicator
	{
		// Immutable-once-built model of a closed bar.
		private sealed class FpBar
		{
			public int		Index;
			public DateTime	TimeUtc;
			public double	Open, High, Low, Close;
			public long		TotalVolume, Delta, MinDelta, MaxDelta, CumulativeDelta;
			public long[][]	Ladder; // each row: { tickIndex, bid, ask }
		}

		private double	bestBid, bestAsk, lastTradePrice;
		private bool	lastWasBuy;
		private readonly Dictionary<long, long> curBid = new Dictionary<long, long>(); // tickIndex -> sell vol
		private readonly Dictionary<long, long> curAsk = new Dictionary<long, long>(); // tickIndex -> buy  vol
		private long	runningDelta, minDelta, maxDelta;	// intra-bar excursion
		private long	cumulativeDelta;					// session running total
		private readonly List<FpBar> bars = new List<FpBar>(); // ring buffer of closed bars
		private volatile string	snapshotJson = "";			// single-builder groundwork (read by HTTP thread in Stage D)
		private string	statusLine = "OCM Bridge: waiting for data…";
		private bool			chartCaptureDone;			// Stage A: one-shot smoke capture succeeded
		private volatile bool	captureInFlight;			// re-entrancy guard for the Dispatcher capture

		// Stage B: on-demand loopback HTTP layer (mirrors the Quantower ClaudeChartObserver).
		private const string	Version = "ocm-bridge/0.4.2-recompile";
		private const int		PortStart = 5670, PortEnd = 5679;	// clear of Quantower's 555x
		private HttpListener			httpListener;
		private int						httpPort;					// 0 = not started
		private CancellationTokenSource	httpCts;
		private Task					httpLoop;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Description			= @"OCM Footprint Bridge — reconstructs bid/ask-at-price from OnMarketData (no Volumetric) and writes a rolling last-N-bars JSON snapshot for the Claude MCP server. Read-only market data.";
				Name				= "OCM Footprint Bridge";
				Calculate			= Calculate.OnEachTick;	// OnMarketData requires realtime tick processing
				IsOverlay			= true;
				IsChartOnly			= true;
				DrawOnPricePanel	= false;
				BarsRequiredToPlot	= 0;

				EnableSnapshot		= true;
				SnapshotBars		= 30;
				// Portable defaults: resolve under the current user's Documents\NinjaTrader 8\OCM.
				// Both folders remain editable in the indicator's properties.
				SnapshotFolder		= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "OCM", "footprint_snapshots");

				EnableChartCapture	= true;
				ScreenshotFolder	= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NinjaTrader 8", "OCM", "screenshots");

				EnableHttp			= true;
				AllowRecompile		= true;
			}
			else if (State == State.DataLoaded)
			{
				// Start the loopback HTTP listener once the indicator is wired to a chart.
				// The /screenshot handler null-checks ChartControl, so the chart need not be
				// fully rendered yet; it will be by the time a request arrives.
				if (EnableHttp && httpListener == null)
					StartHttp();
			}
			else if (State == State.Terminated)
			{
				StopHttp();
			}
		}

		// Realtime market-data events. Same series thread as OnBarUpdate, so no locking yet
		// (the single-builder/volatile pattern is the groundwork for the Stage D HTTP thread).
		protected override void OnMarketData(MarketDataEventArgs e)
		{
			if (e.MarketDataType == MarketDataType.Bid) { bestBid = e.Price; return; }
			if (e.MarketDataType == MarketDataType.Ask) { bestAsk = e.Price; return; }
			if (e.MarketDataType != MarketDataType.Last) return;

			double p = e.Price;
			long   v = e.Volume;
			if (v <= 0) return;

			bool buy;
			if (bestAsk > 0 && p >= bestAsk)		buy = true;		// traded at/above ask -> buyer lifted
			else if (bestBid > 0 && p <= bestBid)	buy = false;	// traded at/below bid -> seller hit
			else if (bestBid > 0 && bestAsk > 0)	buy = p >= (bestBid + bestAsk) / 2.0;
			else if (p > lastTradePrice)			buy = true;		// tick-rule fallback (no quotes yet)
			else if (p < lastTradePrice)			buy = false;
			else									buy = lastWasBuy;

			lastTradePrice	= p;
			lastWasBuy		= buy;

			long key = (long)Math.Round(p / TickSize);
			if (buy)	curAsk[key] = (curAsk.TryGetValue(key, out long a) ? a : 0) + v;
			else		curBid[key] = (curBid.TryGetValue(key, out long b) ? b : 0) + v;

			runningDelta += buy ? v : -v;
			if (runningDelta < minDelta) minDelta = runningDelta;
			if (runningDelta > maxDelta) maxDelta = runningDelta;
		}

		protected override void OnBarUpdate()
		{
			if (BarsInProgress != 0)
				return;

			// New bar => the bar at barsAgo 1 just closed. Finalize it, then reset for the new bar.
			if (IsFirstTickOfBar && CurrentBar >= 1)
			{
				try { FinalizeClosedBar(); }
				catch (Exception ex) { Log("OCM Footprint Bridge error: " + ex.Message, LogLevel.Warning); }

				curBid.Clear();
				curAsk.Clear();
				runningDelta = 0;
				minDelta	 = 0;
				maxDelta	 = 0;

				// Session reset: if the new (forming) bar opens a session, cumulative delta restarts.
				if (Bars.IsFirstBarOfSession)
					cumulativeDelta = 0;
			}

			// Stage A smoke: once we're live, prove Chart.GetScreenshot renders a real PNG
			// (occlusion-proof, Direct2D-correct) before building the HTTP layer. One-shot.
			if (EnableChartCapture && !chartCaptureDone && !captureInFlight
				&& State == State.Realtime && CurrentBar >= 1)
			{
				captureInFlight = true;
				CaptureChartPng("smokeA");
			}

			Draw.TextFixed(this, "ocm_status", statusLine, TextPosition.BottomLeft);
		}

		private void FinalizeClosedBar()
		{
			SortedSet<long> keys = new SortedSet<long>();
			foreach (long k in curBid.Keys) keys.Add(k);
			foreach (long k in curAsk.Keys) keys.Add(k);

			long sumBid = 0, sumAsk = 0;
			long[][] ladder = new long[keys.Count][];
			int i = 0;
			foreach (long k in keys)
			{
				long bid = curBid.TryGetValue(k, out long bb) ? bb : 0;
				long ask = curAsk.TryGetValue(k, out long aa) ? aa : 0;
				sumBid += bid;
				sumAsk += ask;
				ladder[i++] = new long[] { k, bid, ask };
			}

			long delta = sumAsk - sumBid;
			cumulativeDelta += delta;

			FpBar bar = new FpBar
			{
				Index			= CurrentBar - 1,
				TimeUtc			= Time[1].ToUniversalTime(),
				Open			= Open[1],
				High			= High[1],
				Low				= Low[1],
				Close			= Close[1],
				TotalVolume		= (long)Volume[1],
				Delta			= delta,
				MinDelta		= minDelta,
				MaxDelta		= maxDelta,
				CumulativeDelta	= cumulativeDelta,
				Ladder			= ladder
			};

			bars.Add(bar);
			int cap = Math.Max(1, SnapshotBars);
			while (bars.Count > cap)
				bars.RemoveAt(0);

			snapshotJson = BuildSnapshotJson();
			if (EnableSnapshot)
				WriteSnapshot(snapshotJson);

			statusLine = "OCM Bridge: " + bar.TimeUtc.ToLocalTime().ToString("HH:mm:ss")
				+ "  bars=" + bars.Count + "  vol=" + bar.TotalVolume + "  Δ=" + delta + "  cumΔ=" + cumulativeDelta;
		}

		private string BuildSnapshotJson()
		{
			double tick = TickSize;
			StringBuilder sb = new StringBuilder(16384);
			sb.Append("{");
			sb.Append("\"schema\":\"ocm-footprint/v1\",");
			sb.Append("\"instrument\":").Append(JsonStr(Instrument != null ? Instrument.FullName : "")).Append(",");
			sb.Append("\"barsPeriod\":").Append(JsonStr(BarsPeriod.ToString())).Append(",");
			sb.Append("\"tickSize\":").Append(Num(tick)).Append(",");
			sb.Append("\"generatedUtc\":").Append(JsonStr(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))).Append(",");
			sb.Append("\"httpPort\":").Append(httpPort > 0 ? httpPort.ToString(CultureInfo.InvariantCulture) : "null").Append(",");
			int? rr = ComputeRangeRemaining();
			sb.Append("\"rangeRemaining\":").Append(rr.HasValue ? rr.Value.ToString(CultureInfo.InvariantCulture) : "null").Append(",");
			sb.Append("\"bars\":[");
			for (int bi = 0; bi < bars.Count; bi++)
			{
				FpBar b = bars[bi];
				if (bi > 0) sb.Append(",");
				sb.Append("{");
				sb.Append("\"index\":").Append(b.Index.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"timeUtc\":").Append(JsonStr(b.TimeUtc.ToString("o", CultureInfo.InvariantCulture))).Append(",");
				sb.Append("\"open\":").Append(Num(b.Open)).Append(",\"high\":").Append(Num(b.High)).Append(",\"low\":").Append(Num(b.Low)).Append(",\"close\":").Append(Num(b.Close)).Append(",");
				sb.Append("\"direction\":").Append(JsonStr(b.Close >= b.Open ? "up" : "down")).Append(",");
				sb.Append("\"totalVolume\":").Append(b.TotalVolume.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"delta\":").Append(b.Delta.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"minDelta\":").Append(b.MinDelta.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"maxDelta\":").Append(b.MaxDelta.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"cumulativeDelta\":").Append(b.CumulativeDelta.ToString(CultureInfo.InvariantCulture)).Append(",");
				sb.Append("\"ladder\":[");
				for (int li = 0; li < b.Ladder.Length; li++)
				{
					long[] row = b.Ladder[li];
					long bid = row[1], ask = row[2];
					if (li > 0) sb.Append(",");
					sb.Append("{\"price\":").Append(Num(row[0] * tick))
						.Append(",\"bid\":").Append(bid.ToString(CultureInfo.InvariantCulture))
						.Append(",\"ask\":").Append(ask.ToString(CultureInfo.InvariantCulture))
						.Append(",\"delta\":").Append((ask - bid).ToString(CultureInfo.InvariantCulture))
						.Append("}");
				}
				sb.Append("]");
				sb.Append("}");
			}
			sb.Append("]}");
			return sb.ToString();
		}

		private void WriteSnapshot(string json)
		{
			if (string.IsNullOrWhiteSpace(SnapshotFolder))
				return;
			Directory.CreateDirectory(SnapshotFolder);

			string safeInstr = Instrument != null ? Instrument.MasterInstrument.Name : "UNKNOWN";
			string fileName  = safeInstr + "_" + BarsPeriod.Value + BarsPeriod.BarsPeriodType + ".json";
			foreach (char ch in Path.GetInvalidFileNameChars())
				fileName = fileName.Replace(ch, '_');

			string dest = Path.Combine(SnapshotFolder, fileName);
			string tmp  = dest + ".tmp";
			File.WriteAllText(tmp, json, new UTF8Encoding(false));
			File.Copy(tmp, dest, true);
			File.Delete(tmp);
		}

		// --- Bridge-side chart capture (Stage A) ----------------------------------------------
		// Renders the chart via NinjaTrader's OWN public screenshot facility — occlusion-proof
		// and Direct2D-correct, unlike screen-grab (PrintWindow returns black on the DirectX
		// swap chain). GetScreenshot/RenderTargetBitmap are WPF UI-thread-bound, so marshal onto
		// the ChartControl's Dispatcher. PNG encode mirrors NT's own Share pipeline.
		// Smoke (Stage A): one-shot capture to a file, off the data thread (InvokeAsync).
		private void CaptureChartPng(string reason)
		{
			var cc = ChartControl;
			if (cc == null) { captureInFlight = false; return; }
			cc.Dispatcher.InvokeAsync(() =>
			{
				try
				{
					byte[] png = CaptureChartPngBytes();
					if (png == null) { Log("OCM capture: null bitmap (chart not ready?)", LogLevel.Warning); return; }
					WriteScreenshot(png, reason);
					chartCaptureDone = true;
					Log("OCM capture OK (" + reason + "): " + png.Length + " bytes -> " + ScreenshotFolder, LogLevel.Information);
				}
				catch (Exception ex) { Log("OCM capture error: " + ex.Message, LogLevel.Warning); }
				finally { captureInFlight = false; }
			});
		}

		// On-demand (Stage B): capture fresh PNG bytes synchronously for the HTTP handler.
		// Called on an HTTP worker thread; marshals the render onto the UI/Dispatcher thread.
		private byte[] CaptureChartPngOnUi()
		{
			var cc = ChartControl;
			if (cc == null) return null;
			byte[] png = null;
			cc.Dispatcher.Invoke((Action)(() => { try { png = CaptureChartPngBytes(); } catch { png = null; } }));
			return png;
		}

		// MUST run on the UI/Dispatcher thread: GetScreenshot + RenderTargetBitmap are WPF
		// UI-thread-bound. Renders NT's retained visual tree => occlusion-proof, Direct2D-correct.
		private byte[] CaptureChartPngBytes()
		{
			var cc = ChartControl;
			if (cc == null) return null;
			var chart = cc.OwnerChart;
			if (chart == null) return null;
			var rtb = chart.GetScreenshot(ShareScreenshotType.Window, null);
			if (rtb == null) return null;
			return EncodePng(rtb);
		}

		private static byte[] EncodePng(System.Windows.Media.Imaging.RenderTargetBitmap rtb)
		{
			var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
			enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
			using (MemoryStream ms = new MemoryStream())
			{
				enc.Save(ms);
				return ms.ToArray();
			}
		}

		private void WriteScreenshot(byte[] png, string reason)
		{
			if (string.IsNullOrWhiteSpace(ScreenshotFolder))
				return;
			Directory.CreateDirectory(ScreenshotFolder);

			string dest = Path.Combine(ScreenshotFolder, InstrumentPeriodKey() + "_" + reason + ".png");
			string tmp  = dest + ".tmp";
			File.WriteAllBytes(tmp, png);
			File.Copy(tmp, dest, true);
			File.Delete(tmp);
		}

		// --- Stage B: on-demand HTTP layer (loopback only; READ-ONLY market data; no orders) -----
		private void StartHttp()
		{
			for (int port = PortStart; port <= PortEnd; port++)
			{
				try
				{
					var listener = new HttpListener();
					listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
					listener.Start();
					httpListener = listener;
					httpPort     = port;
					httpCts      = new CancellationTokenSource();
					httpLoop     = Task.Run(() => HttpAcceptLoopAsync(httpCts.Token));
					WritePortFile();
					Log("OCM Bridge HTTP listening on http://127.0.0.1:" + port + "/ (screenshot,footprint,health)", LogLevel.Information);
					return;
				}
				catch (Exception ex)
				{
					Log("OCM Bridge HTTP bind " + port + " failed: " + ex.Message, LogLevel.Warning);
				}
			}
			Log("OCM Bridge HTTP: no free port in " + PortStart + "-" + PortEnd, LogLevel.Warning);
		}

		private void StopHttp()
		{
			try { httpCts?.Cancel(); }       catch { }
			try { httpListener?.Stop(); }     catch { }
			try { httpListener?.Close(); }    catch { }
			try { httpCts?.Dispose(); }       catch { }
			httpListener = null;
			httpPort     = 0;
			httpCts      = null;
		}

		private async Task HttpAcceptLoopAsync(CancellationToken ct)
		{
			var listener = httpListener;
			if (listener == null) return;
			while (!ct.IsCancellationRequested && listener.IsListening)
			{
				HttpListenerContext ctx;
				try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
				catch (HttpListenerException)   { break; }	// listener stopped
				catch (ObjectDisposedException) { break; }
				catch (Exception ex) { Log("OCM HTTP accept error: " + ex.Message, LogLevel.Warning); continue; }
				_ = Task.Run(() => HandleHttpRequest(ctx));	// don't stall the accept loop
			}
		}

		private void HandleHttpRequest(HttpListenerContext ctx)
		{
			try
			{
				string path = (ctx.Request.Url != null ? ctx.Request.Url.AbsolutePath : "/").TrimEnd('/');
				if (path == "") path = "/";
				switch (path)
				{
					case "/":           ServeIndex(ctx);      break;
					case "/health":     ServeHealth(ctx);     break;
					case "/screenshot": ServeScreenshot(ctx); break;
					case "/footprint":  ServeFootprint(ctx);  break;
					case "/recompile":  ServeRecompile(ctx);  break;
					default:            WriteText(ctx, 404, "application/json", "{\"error\":\"not found\"}"); break;
				}
			}
			catch (Exception ex)
			{
				try { WriteText(ctx, 500, "application/json", "{\"error\":" + JsonStr(ex.Message) + "}"); } catch { }
				Log("OCM HTTP handler error: " + ex.Message, LogLevel.Warning);
			}
			finally { try { ctx.Response.Close(); } catch { } }
		}

		private void ServeScreenshot(HttpListenerContext ctx)
		{
			byte[] png = null;
			try { png = CaptureChartPngOnUi(); }
			catch (Exception ex) { Log("OCM /screenshot capture error: " + ex.Message, LogLevel.Warning); }
			if (png == null || png.Length == 0)
			{
				WriteText(ctx, 503, "application/json", "{\"error\":\"chart not available for capture\"}");
				return;
			}
			WriteBytes(ctx, 200, "image/png", png);
		}

		private void ServeFootprint(HttpListenerContext ctx)
		{
			string json = snapshotJson;
			if (string.IsNullOrEmpty(json)) { WriteText(ctx, 503, "application/json", "{\"error\":\"no snapshot yet\"}"); return; }
			WriteText(ctx, 200, "application/json", json);
		}

		// Dev-loop Goal 2 — in-process COMPILE VALIDATOR (honest scope, verified 2026-06-03).
		// WHAT WORKS: this compiles the installed Custom code in NinjaTrader's own Roslyn compiler
		// (NinjaTrader.Code.Compiler.Compile, public static) and reports errors to
		// recompile_result.json — NO manual F5 needed to catch compile errors, across ALL Custom
		// files (stronger than the single-file external compile_check.ps1).
		// WHAT DOES NOT WORK (roadblock, tested): the in-process HOT-RELOAD. Compile() emits the
		// new assembly and InvokeCompileCompleted() raises CompileCompleted, but NT does NOT
		// recreate the on-chart indicator from the new assembly (verified via log: no instance
		// restart after "firing reload"). NT ties the load-new-assembly + rebuild-NinjaScript step
		// to its editor/Control-Center orchestration (EditorViewModel.OnCompile), not the public
		// Compiler surface. => LOADING new code still needs a manual F5. See JOURNAL for the RCA.
		// Loopback-only dev action; READ-ONLY market data, no account/orders.
		private void ServeRecompile(HttpListenerContext ctx)
		{
			if (!AllowRecompile)
			{
				WriteText(ctx, 403, "application/json", "{\"error\":\"recompile disabled (AllowRecompile=false)\"}");
				return;
			}
			WriteText(ctx, 202, "application/json",
				"{\"status\":\"triggered\",\"note\":\"NinjaScript recompiling; bridge will reload — poll /health until it returns, then read recompile_result.json\"}");

			var cc = ChartControl;
			if (cc == null) { Log("OCM /recompile: ChartControl null; cannot reach Dispatcher", LogLevel.Warning); return; }
			cc.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, (Action)(() =>
			{
				try { TriggerRecompile(); }
				catch (Exception ex) { Log("OCM recompile error: " + ex.Message, LogLevel.Warning); }
			}));
		}

		// MUST run on the UI/Dispatcher thread. Invokes NinjaTrader.Code.Compiler.Compile via
		// reflection (avoids any compile-time reference to the Roslyn EmitResult type), reads
		// Success/Diagnostics reflectively, and writes the result file synchronously BEFORE the
		// (async) reload tears this instance down. On a FAILED compile NT keeps the last-good
		// assembly and this instance survives, so the error file is reliably written.
		private void TriggerRecompile()
		{
			Type compilerType = null;
			foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (a.GetName().Name == "NinjaTrader.Core") { compilerType = a.GetType("NinjaTrader.Code.Compiler"); if (compilerType != null) break; }
			}
			if (compilerType == null) { WriteRecompileResult(false, "NinjaTrader.Code.Compiler not found"); return; }

			MethodInfo compile = compilerType.GetMethod("Compile", BindingFlags.Public | BindingFlags.Static);
			if (compile == null) { WriteRecompileResult(false, "Compiler.Compile(...) not found"); return; }

			Log("OCM Bridge: recompile triggered (Compiler.Compile)…", LogLevel.Information);
			// Compile(checkCompileOnly:false, debugBuild:false, filesToIgnore:[], filesInTmp:[])
			object emit = compile.Invoke(null, new object[] { false, false, new string[0], new string[0] });

			bool success = false;
			StringBuilder errs = new StringBuilder();
			if (emit != null)
			{
				PropertyInfo sp = emit.GetType().GetProperty("Success");
				if (sp != null) success = (bool)sp.GetValue(emit);
				PropertyInfo dp = emit.GetType().GetProperty("Diagnostics");
				if (dp != null && dp.GetValue(emit) is System.Collections.IEnumerable diags)
				{
					// Errors only — Roslyn emits thousands of benign CS1701 mscorlib-version warnings.
					foreach (object d in diags)
					{
						if (d == null) continue;
						PropertyInfo sev = d.GetType().GetProperty("Severity");
						if (sev != null && string.Equals(sev.GetValue(d) != null ? sev.GetValue(d).ToString() : "", "Error", StringComparison.Ordinal))
							errs.Append(d.ToString()).Append("\n");
					}
				}
			}
			WriteRecompileResult(success, errs.ToString());
			Log("OCM Bridge: compile " + (success ? "OK" : "FAILED") + (success ? " — firing reload" : ""), success ? LogLevel.Information : LogLevel.Warning);

			// Compile() only EMITS the assembly; the in-process RELOAD (re-instantiating NinjaScript
			// from it) is driven by the CompileCompleted event, raised by the internal
			// InvokeCompileCompleted(). Without this, the new code compiles but never loads.
			// NB: this reloads ALL NinjaScript and tears down THIS instance — so it runs LAST,
			// after the result file is already written.
			if (success)
			{
				MethodInfo invoke = compilerType.GetMethod("InvokeCompileCompleted", BindingFlags.NonPublic | BindingFlags.Static);
				if (invoke != null) invoke.Invoke(null, null);
				else Log("OCM Bridge: InvokeCompileCompleted not found — compiled but NOT reloaded", LogLevel.Warning);
			}
		}

		private void WriteRecompileResult(bool success, string errors)
		{
			try
			{
				string ocmRoot = Directory.GetParent(SnapshotFolder) != null ? Directory.GetParent(SnapshotFolder).FullName : SnapshotFolder;
				Directory.CreateDirectory(ocmRoot);
				string dest = Path.Combine(ocmRoot, "recompile_result.json");
				string json = "{\"success\":" + (success ? "true" : "false")
					+ ",\"utc\":" + JsonStr(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture))
					+ ",\"version\":" + JsonStr(Version)
					+ ",\"errors\":" + JsonStr(errors ?? "") + "}";
				File.WriteAllText(dest, json, new UTF8Encoding(false));
			}
			catch (Exception ex) { Log("OCM recompile-result write error: " + ex.Message, LogLevel.Warning); }
		}

		private void ServeHealth(HttpListenerContext ctx)
		{
			string instr  = Instrument != null ? Instrument.FullName : "";
			string period = BarsPeriod != null ? BarsPeriod.ToString() : "";
			string json = "{\"ok\":true,\"version\":" + JsonStr(Version) + ",\"port\":" + httpPort
				+ ",\"instrument\":" + JsonStr(instr) + ",\"barsPeriod\":" + JsonStr(period)
				+ ",\"bars\":" + bars.Count + "}";
			WriteText(ctx, 200, "application/json", json);
		}

		private void ServeIndex(HttpListenerContext ctx)
		{
			WriteText(ctx, 200, "text/plain",
				"OCM Footprint Bridge " + Version + " port " + httpPort + "\n"
				+ "GET /screenshot - live whole-window chart PNG (on-demand)\n"
				+ "GET /footprint  - latest rolling footprint JSON\n"
				+ "GET /recompile  - trigger NinjaScript compile+reload (dev; AllowRecompile=" + AllowRecompile + ")\n"
				+ "GET /health     - liveness + instrument/period/port\n");
		}

		private static void WriteBytes(HttpListenerContext ctx, int status, string contentType, byte[] body)
		{
			ctx.Response.StatusCode      = status;
			ctx.Response.ContentType     = contentType;
			ctx.Response.Headers["Cache-Control"] = "no-store";
			ctx.Response.ContentLength64 = body.Length;
			using (Stream os = ctx.Response.OutputStream)
				os.Write(body, 0, body.Length);
		}

		private static void WriteText(HttpListenerContext ctx, int status, string contentType, string text)
		{
			WriteBytes(ctx, status, contentType, Encoding.UTF8.GetBytes(text));
		}

		private string InstrumentPeriodKey()
		{
			string safeInstr = Instrument != null ? Instrument.MasterInstrument.Name : "UNKNOWN";
			string key = safeInstr + "_" + BarsPeriod.Value + BarsPeriod.BarsPeriodType;
			foreach (char ch in Path.GetInvalidFileNameChars())
				key = key.Replace(ch, '_');
			return key;
		}

		private void WritePortFile()
		{
			try
			{
				if (string.IsNullOrWhiteSpace(SnapshotFolder)) return;
				Directory.CreateDirectory(SnapshotFolder);
				string dest = Path.Combine(SnapshotFolder, InstrumentPeriodKey() + ".port");
				File.WriteAllText(dest, httpPort.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false));
			}
			catch (Exception ex) { Log("OCM port-file write error: " + ex.Message, LogLevel.Warning); }
		}

		// Range bars only: ticks of range remaining in the forming bar.
		private int? ComputeRangeRemaining()
		{
			int rangeTicks = 0;
			if (BarsPeriod.BaseBarsPeriodType == BarsPeriodType.Range)		rangeTicks = BarsPeriod.BaseBarsPeriodValue;
			else if (BarsPeriod.BarsPeriodType == BarsPeriodType.Range)		rangeTicks = BarsPeriod.Value;
			else return null;

			if (rangeTicks <= 0 || CurrentBar < 0)
				return null;

			int span = (int)Math.Round((High[0] - Low[0]) / TickSize);
			int remaining = rangeTicks - span;
			return remaining < 0 ? 0 : remaining;
		}

		private static string Num(double d)
		{
			return d.ToString("0.#########", CultureInfo.InvariantCulture);
		}

		private static string JsonStr(string s)
		{
			if (s == null)
				return "null";
			StringBuilder sb = new StringBuilder(s.Length + 2);
			sb.Append('"');
			foreach (char c in s)
			{
				switch (c)
				{
					case '"':	sb.Append("\\\""); break;
					case '\\':	sb.Append("\\\\"); break;
					case '\b':	sb.Append("\\b"); break;
					case '\f':	sb.Append("\\f"); break;
					case '\n':	sb.Append("\\n"); break;
					case '\r':	sb.Append("\\r"); break;
					case '\t':	sb.Append("\\t"); break;
					default:
						if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
						else sb.Append(c);
						break;
				}
			}
			sb.Append('"');
			return sb.ToString();
		}

		#region Properties
		[Display(Name = "Enable Snapshot", GroupName = "Bridge", Order = 0)]
		public bool EnableSnapshot { get; set; }

		[Range(1, 500)]
		[Display(Name = "Snapshot Bars", GroupName = "Bridge", Order = 1)]
		public int SnapshotBars { get; set; }

		[Display(Name = "Snapshot Folder", GroupName = "Bridge", Order = 2)]
		public string SnapshotFolder { get; set; }

		[Display(Name = "Enable Chart Capture", GroupName = "Bridge", Order = 3)]
		public bool EnableChartCapture { get; set; }

		[Display(Name = "Screenshot Folder", GroupName = "Bridge", Order = 4)]
		public string ScreenshotFolder { get; set; }

		[Display(Name = "Enable HTTP (on-demand)", GroupName = "Bridge", Order = 5)]
		public bool EnableHttp { get; set; }

		[Display(Name = "Allow Recompile (dev)", GroupName = "Bridge", Order = 6)]
		public bool AllowRecompile { get; set; }
		#endregion
	}
}

// NOTE: the NinjaScript auto-generated wrapper (cache + factory methods) is
// intentionally NOT included here. NinjaTrader's editor generates that block on
// compile; hand-authoring it caused a duplicated wrapper (CS0102/CS0111/CS0121)
// when the file was overwritten and recompiled. Let NinjaTrader own it.
