using MultiMon.Control.Shared;
using MultiMon.Core.Diagnostics;
using MultiMon.Core.Models;

namespace MultiMon.Control.Mac;

/// <summary>
/// Dev switch <c>--autoperform=&lt;clip&gt;[,seconds]</c>: loads the clip on monitor 1, performs for a few
/// seconds, stops and exits. It drives the SAME <see cref="MainViewModel"/> the user's buttons drive
/// (LESSON-TEST-004: the gate must call the path the user clicks, not a lookalike), so a headless run in
/// CI or over SSH proves the real ApplyShow → Performing → Idle sequence and a clean, ordered teardown.
/// It exits through <see cref="App.RequestExitAsync"/> — the same ordered path a window close takes — so
/// the run it gates includes the real teardown. Documented in docs/MAC_SETUP.md §5; the app behaves
/// exactly as before when the flag is absent.
/// </summary>
internal static class AutoPerform
{
    public const string Flag = "--autoperform=";

    private static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(3);
    /// <summary>How long to wait for the controller to actually reach Performing before calling it a wedge.</summary>
    private static readonly TimeSpan EnterTimeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(MainViewModel vm, ILog log, string argument, App app)
    {
        try
        {
            var parts = argument.Split(',', 2);
            var clip = parts[0];
            var hold = parts.Length > 1 && double.TryParse(parts[1], out var s) && s > 0
                ? TimeSpan.FromSeconds(s)
                : DefaultHold;

            if (!File.Exists(clip))
            {
                log.Error("AutoPerform", $"Clip not found: '{clip}'.");
                await app.RequestExitAsync(2);
                return;
            }

            log.Info("AutoPerform", $"clip='{clip}' hold={hold.TotalSeconds:0.#}s mode={ShowMode.Individual}");
            vm.SelectedMode = ShowMode.Individual;
            vm.Rows[0].FilePath = clip;

            vm.Perform();
            if (!await WaitForAsync(() => vm.IsPerforming, EnterTimeout))
            {
                log.Error("AutoPerform", $"Did not reach Performing within {EnterTimeout.TotalSeconds:0}s — status: {vm.Status}");
                await app.RequestExitAsync(3);
                return;
            }
            log.Info("AutoPerform", $"performing — {vm.Status}");

            await Task.Delay(hold);
            vm.Stop();
            if (!await WaitForAsync(() => !vm.IsPerforming, EnterTimeout))
            {
                log.Error("AutoPerform", $"Did not return to Idle within {EnterTimeout.TotalSeconds:0}s — status: {vm.Status}");
                await app.RequestExitAsync(4);
                return;
            }
            log.Info("AutoPerform", $"stopped — {vm.Status}; shutting down");
            await app.RequestExitAsync();
        }
        catch (Exception ex)
        {
            log.Error("AutoPerform", $"failed: {ex}");
            await app.RequestExitAsync(5);
        }
    }

    /// <summary>Polls a UI-thread-owned flag from the UI thread, awaiting between checks so the run loop
    /// keeps pumping (the controller's state change arrives through the dispatcher).</summary>
    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;
            await Task.Delay(50);
        }
        return condition();
    }
}
