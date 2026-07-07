using Act2;

// ── PUBLIC IMAGE ENTRYPOINT ──────────────────────────────────────────────────
// This program is what ships in the submitted container. It compiles ONLY the
// harness contract: task I/O, Fireworks client, category prompts, model
// preference. The measurement toolkit (classifier/analyzer/costsweep/baselines)
// is NOT part of this build — the public image must be assumed decompiled.

string? hin = null, hout = null, hmodel = null;
int par = 8, perTask = 25, wallS = 540;
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
        default: Console.Error.WriteLine($"unknown arg: {k}"); return 1;
    }
}
return await Harness.RunAsync(hin, hout, par, perTask, wallS, hmodel);
