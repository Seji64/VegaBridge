using Serilog;
using Shiny.BluetoothLE;
using VegaBridgeApp.Models.BLE;
using VegaBridgeApp.Services.BLE.Plugins;
using VegaBridgeApp.Services.Debug;

namespace VegaBridgeApp.Services.BLE;

/// <summary>Outcome of the 25-minute W2R live-profile density test (see <see cref="W2rRouteSim"/>).</summary>
public sealed record W2rRouteSimResult(
    int Ticks, int SlowTicks, int FailedTicks, int MaxDrainMs, int? FirstAnomalySecond, string Summary);

/// <summary>
/// 25-minute live-profile density test over the real W2R write path:
/// navigation start (DEST + REM, like a real ride) → 1 Hz SM ticks with
/// a simulated urban maneuver change every ~30 s (NAVI and SM1 on-change
/// only; straight maneuvers skip SM1) → FINISH. Traffic volume is
/// ≈77 frames/min (SM @ 1 Hz = 60, SM1 on-change ≈ 11/min, NAVI ≈ 2/min,
/// PING/15 s = 4, sent by the sim loop – in production the coordinator's
/// 15 s tick owns the keepalive cadence). I.e. the traffic profile a live
/// ride produces after the on-change traffic reduction – no bike needed.
/// The question it answers: does the connection survive ~25 minutes
/// (≈20 min is the known critical point) at live-ride traffic density?
/// No UI dependency, so it also works with the display off; every tick,
/// failure, link loss/rebuild and the final summary are logged under
/// "W2R-SIM".
/// </summary>
public sealed class W2rRouteSim(BleManagerService bleManager)
{
    public async Task<W2rRouteSimResult> RunAsync(double? startLat, double? startLon, CancellationToken ct)
    {
        if (bleManager.ActiveLink is not { } link)
            return new W2rRouteSimResult(0, 0, 0, 0, null, "W2R-SIM: no connected device");

        const int totalSec = 25 * 60;     // 25 min, one 1 s tick per second
        const int intervalSec = 1;        // 1 Hz = the live-ride SM cadence
        const int naviEvery = 30;         // simulated urban maneuver change (NAVI on-change)
        const int slowThresholdMs = 500;  // a clogged W2R queue shows up as ~4 s drains

        // The in-memory DebugLogSink is off by default – a test that produces
        // no capturable log is useless, so the test owns its capture.
        DebugLogSink.Instance.SetEnabled(true);
        DebugLogSink.Instance.Clear();

        // The sink buffer lives in RAM; a 25-min display-off run can outlive
        // the app process (which is exactly the phenomenon under test). The
        // W2R-SIM timeline is therefore additionally appended to a file that
        // survives a process kill.
        string simLogFile = System.IO.Path.Combine(
            Microsoft.Maui.Storage.FileSystem.AppDataDirectory,
            $"w2r-sim-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        IBleConnectedDevice wrapper = bleManager.CreateConnectedDevice(link.Peripheral, link.Plugin);
        IPeripheral? livePeripheral = link.Peripheral;
        IBleDevicePlugin? livePlugin = link.Plugin;

        // Wall clock alongside test time: while iOS suspends the app the
        // Task.Delay loop freezes, so t+ stalls while wall keeps running –
        // the gap between the two reveals the suspension duration.
        DateTime simStartUtc = DateTime.UtcNow;
        void SimLine(string line)
        {
            Log.Information("BLE-LOGGER: {Line}", line);
            _ = System.IO.File.AppendAllTextAsync(simLogFile, line + Environment.NewLine);
        }
        int WallSec() => (int)(DateTime.UtcNow - simStartUtc).TotalSeconds;

        SimLine($"W2R-SIM START: 25-min live-profile test, {totalSec} ticks @ 1 Hz, SM every tick + NAVI every {naviEvery}s + SM1 on-change + PING/15s in sim loop ≈ 77 frames/min, slow threshold {slowThresholdMs} ms, timeline file {simLogFile}");

        int ticks = 0, slowTicks = 0, failedTicks = 0, maxDrainMs = 0;
        int firstAnomalySecond = 0;

        try
        {
            // Navigation start (DEST + REM), same as a real ride. The PING
            // keepalive is sent explicitly by the sim loop every 15 ticks –
            // in production the coordinator's 15 s tick owns the cadence,
            // but the sim bypasses the coordinator and drives the plugin.
            await livePlugin.SendNavigationStartAsync(wrapper, new NavigationStartInput
            {
                TotalDistanceKm = 20,
                TotalTimeMin = totalSec / 60,
                StartLatitude = startLat,
                StartLongitude = startLon
            });

            for (int i = 1; i <= totalSec; i++)
            {
                ct.ThrowIfCancellationRequested();
                ticks++;
                int second = i - 1; // t+ elapsed seconds at this tick

                // Reconnect-proofing: Shiny owns link recovery; an in-session
                // reconnect reassigns the manager's live link mid-run. While
                // the link is down, skip the write (counted as a failed
                // tick); when a new connection appears, rebuild the wrapper
                // so updates keep flowing on the live link.
                if (bleManager.ActiveLink is not { } liveLink)
                {
                    // User-initiated disconnect ends the test: Shiny keeps it
                    // disconnected on purpose, waiting 25 min here would just
                    // log 25 LINK DOWN lines.
                    if (bleManager.IsUserInitiatedDisconnect)
                    {
                        string abortSummary = $"W2R-SIM ABORTED after {ticks}/{totalSec} ticks (user disconnect), {slowTicks} slow / {failedTicks} failed, max drain {maxDrainMs} ms";
                        SimLine($"W2R-SIM ABORT t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} – user-initiated disconnect");
                        SimLine(abortSummary);
                        return new W2rRouteSimResult(ticks, slowTicks, failedTicks, maxDrainMs, null, abortSummary);
                    }
                    failedTicks++;
                    if (firstAnomalySecond == 0)
                        firstAnomalySecond = second;
                    SimLine($"W2R-SIM LINK DOWN t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} – write skipped (Shiny reconnect in progress)");
                    await Task.Delay(intervalSec, ct);
                    continue;
                }
                if (!ReferenceEquals(livePeripheral, liveLink.Peripheral) || !ReferenceEquals(livePlugin, liveLink.Plugin))
                {
                    livePeripheral = liveLink.Peripheral;
                    livePlugin = liveLink.Plugin;
                    wrapper = bleManager.CreateConnectedDevice(livePeripheral, livePlugin);
                    SimLine($"W2R-SIM LINK REBUILT t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} – continuing on the new connection");
                }

                // Live-ride profile: a simulated urban "maneuver" advances
                // every {naviEvery} s – the NAVI frame goes out on that
                // change only (on-change policy), while SM rides on every
                // 1 s tick (its distance field changes every tick). SM1 is
                // gated on-change in the plugin: distance counts down
                // 300 → 10 m within the maneuver, so its countdown bucket
                // fires ≈ 8 times per maneuver instead of every tick
                // (straight maneuvers skip SM1 entirely).
                int maneuver = (i - 1) / naviEvery; // 0 .. totalSec/naviEvery-1
                bool sendNavi = i % naviEvery == 1; // NAVI on maneuver change
                string icon = maneuver % 3 == 0 ? "turn-left" : maneuver % 3 == 1 ? "straight" : "turn-right";
                int distM = 300 - ((i - 1) % naviEvery) * 10; // 300,290,…,10

                var input = new NavigationUpdateInput
                {
                    ManeuverIcon = icon,
                    InstructionText = $"W2R-LIVE M{maneuver} {icon}",
                    StreetName = $"W2R-LIVE M{maneuver}",
                    DistanceToTurnM = distM,
                    SpeedKmh = 30,
                    RemainingDistanceKm = 20 * (1 - (double)i / totalSec),
                    RemainingTimeMin = (totalSec - i) / 60.0,
                    CurrentManeuverIndex = maneuver,
                    TotalManeuvers = totalSec / naviEvery,
                    IsFinal = i == totalSec
                };

                var sw = System.Diagnostics.Stopwatch.StartNew();
                bool ok;
                try
                {
                    await livePlugin.SendNavigationUpdateAsync(wrapper, input, sendNavi);
                    ok = true;
                }
                catch (Exception ex)
                {
                    ok = false;
                    failedTicks++;
                    SimLine($"W2R-SIM FAIL t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} after {sw.ElapsedMilliseconds} ms ({ex.GetType().Name})");
                }
                int drainMs = (int)sw.ElapsedMilliseconds;
                sw.Stop();
                maxDrainMs = Math.Max(maxDrainMs, drainMs);

                if (ok && drainMs > slowThresholdMs)
                {
                    slowTicks++;
                    if (firstAnomalySecond == 0)
                        firstAnomalySecond = second;
                    SimLine($"W2R-SIM SLOW t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} drain={drainMs} ms");
                }
                else if (ok)
                {
                    SimLine($"W2R-SIM OK t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} drain={drainMs} ms");
                }

                // PING keepalive every 15 ticks – the keepalive component of
                // the live-ride profile. It rides the same W2R queue, so
                // keepalive survival at live-ride density is part of the test.
                if (i % 15 == 0)
                {
                    var pingSw = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        await livePlugin.SendKeepAliveAsync(wrapper);
                        SimLine($"W2R-SIM PING t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} drain={pingSw.ElapsedMilliseconds} ms");
                    }
                    catch (Exception ex)
                    {
                        // A PING failure is not a sim anomaly by itself – the
                        // next tick's LINK DOWN branch tracks the link state.
                        SimLine($"W2R-SIM PING FAIL t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} ({ex.GetType().Name})");
                    }
                }

                // Keep the 1 Hz cadence: the write already consumed part of
                // this interval (a clogged write self-paces at ~4 s).
                long leftoverMs = intervalSec * 1000L - sw.ElapsedMilliseconds;
                if (leftoverMs > 0)
                    await Task.Delay((int)leftoverMs, ct);

                // RX liveness into the DUREABLE timeline: RECV GUI1 lines
                // live only in the RAM debug log, which does not survive a
                // process kill on display-off runs. A stalled/clogged link
                // usually keeps RX alive – the rising rxFrames counter is
                // the evidence that separates "W2R consumer stall" from
                // "whole link is dead".
                if (i % 60 == 0)
                    SimLine($"W2R-SIM HB t+{FormatSimTm(second)} wall+{FormatSimTm(WallSec())} tick={i}/{totalSec} rxFrames={(livePlugin as MvAgustaBlePlugin is { } mv ? mv.RxFrameCount : 0)}");
            }

            if (bleManager.ActiveLink is { } finishLink)
                await finishLink.Plugin.SendNavigationFinishAsync(bleManager.CreateConnectedDevice(finishLink.Peripheral, finishLink.Plugin));
            else
                SimLine("W2R-SIM: FINISH skipped – no active connection");

            string summary = $"W2R-SIM DONE: {ticks}/{totalSec} ticks over {totalSec}s ({FormatSimTm(WallSec())} wall clock, ≈77 frames/min live profile), {slowTicks} slow / {failedTicks} failed"
                + (firstAnomalySecond > 0
                    ? $", first anomaly t+{FormatSimTm(firstAnomalySecond)}"
                    : "")
                + $", max drain {maxDrainMs} ms";
            SimLine(summary);
            return new W2rRouteSimResult(
                ticks, slowTicks, failedTicks, maxDrainMs,
                firstAnomalySecond > 0 ? firstAnomalySecond : null, summary);
        }
        catch (OperationCanceledException)
        {
            SimLine($"W2R-SIM STOPPED (cancelled) at t+{FormatSimTm(ticks * intervalSec)} wall+{FormatSimTm(WallSec())} – sending FINISH");
            if (bleManager.ActiveLink is { } finishLink)
            {
                try
                {
                    await finishLink.Plugin.SendNavigationFinishAsync(bleManager.CreateConnectedDevice(finishLink.Peripheral, finishLink.Plugin));
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "W2R-SIM: FINISH after cancel failed");
                }
            }
            else
            {
                SimLine("W2R-SIM: FINISH skipped – no active connection");
            }
            string summary = $"W2R-SIM STOPPED after {ticks}/{totalSec} ticks, {slowTicks} slow / {failedTicks} failed, max drain {maxDrainMs} ms";
            SimLine(summary);
            return new W2rRouteSimResult(ticks, slowTicks, failedTicks, maxDrainMs, null, summary);
        }

        static string FormatSimTm(int seconds) => $"{seconds / 60:00}:{seconds % 60:00}";
    }
}
