using MultiMon.Stress;

// Headless stress harness entry point — the primary verification gate.
// Usage: dotnet run --project MultiMon.Stress -- --cycles=50 --windows=1 [--fullscreen] [--hap] [--audio]
return await StressHarness.RunAsync(args);
