using Act2;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
    return PeakSelfTest.Run();

// ── PUBLIC IMAGE ENTRYPOINT ──────────────────────────────────────────────────
// This program is what ships in the submitted container. It compiles ONLY the
// harness contract: task I/O, Fireworks client, category prompts, model
// preference. The measurement toolkit (classifier/analyzer/costsweep/baselines)
// is NOT part of this build — the public image must be assumed decompiled.
//
// Rung control: --no-solver disables the deterministic solver lane. R1 ships
// with the flag set (in the Dockerfile CMD); R2 removes it. One binary, one
// variable per rung.

string? hin = null, hout = null, hmodel = null;
// perTask 28s (general rule: responses must be under 30s — the proxy may kill longer calls server-side): with reasoning suppressed, healthy calls return in seconds; a
// call still running at 28s is burning budget on hidden reasoning and will
// blank — cutting it and switching models (see Harness attempt ladder) beats
// waiting. Worst case 19 tasks x 2 attempts x 28s at 8-wide sits far under the
// 540s wall, leaving the watchdog flush margin intact.
int par = 8, perTask = 28, wallS = 540;
bool useSolver = true, lean = false, norm = false, prune = false, terse = false, batch = false, comply = false, retry = false, reroute = false, localOnly = false, localFb = false;
double temp = 0.0;
for (int i = 0; i < args.Length; i++)
{
    string k = args[i];
    if (k == "harness") continue;   // compatibility with `CMD ["harness"]`
    string NV() => i + 1 < args.Length ? args[++i] : "";
    switch (k)
    {
        case "--in": hin = NV(); break;
        case "--out": hout = NV(); break;
        case "--model": hmodel = NV(); break;
        case "--parallel": int.TryParse(NV(), out par); break;
        case "--per-task-seconds": int.TryParse(NV(), out perTask); break;
        case "--wall-seconds": int.TryParse(NV(), out wallS); break;
        case "--no-solver": useSolver = false; break;
        // ── reduction rungs: one flag per variable, risk class in the name ──
        case "--lean-prompts": lean = true; break;       // LOW RISK  (hold for final push)
        case "--normalize-input": norm = true; break;    // LOW RISK  (hold for final push)
        case "--prune-input": prune = true; break;       // HIGH RISK (validate early)
        case "--terse-output": terse = true; break;      // HIGH RISK (validate early)
        case "--batch": batch = true; break;              // HIGH RISK (wrapper-only savings; unbatches on anomaly)
        case "--comply": comply = true; break;            // LOW RISK  (fix-only-when-broken format enforcement)
        case "--retry": retry = true; break;              // 2 attempts per rung before laddering
        case "--reroute": reroute = true; break;          // misroute-insurance classifier on cue-free defaults
        case "--temp": double.TryParse(NV(), System.Globalization.CultureInfo.InvariantCulture, out temp); break;
        case "--local": localOnly = true; break;          // R-ZERO: all non-solver tasks answered on-container, zero Fireworks tokens
        case "--local-fallback": localFb = true; break;   // hybrid: local first, remote ladder as gate insurance
        default: Console.Error.WriteLine($"unknown arg: {k}"); return 1;
    }
}
return await Harness.RunAsync(hin, hout, par, perTask, wallS, hmodel, useSolver, lean, norm, prune, terse, batch, comply, temp, retry, reroute, localOnly, localFb);
