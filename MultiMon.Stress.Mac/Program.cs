using AppKit;
using Foundation;
using MultiMon.Audio.Mac;
using MultiMon.Core.Diagnostics;
using MultiMon.Platform.Mac;
using MultiMon.Stress.Mac;

// Headless stress harness entry point — the Mac primary verification gate.
// Usage: MultiMon.Stress.Mac --cycles=50 --windows=1 [--fullscreen] [--soak-seconds=S] [--audio [--audio-file=PATH]] [--video=CLIP [--video2=CLIP] [--hap] [--force-sw-decode] [--mode=span|individual]] | --source-check [--video=HAP.mov] [--video2=colour_h264.mp4] | --list-monitors | --list-audio
// NSScreen needs the AppKit application object to exist before any display is enumerated.
NSApplication.Init();

if (args.Length == 1 && args[0] == "--list-monitors")
{
    using var service = new MonitorService(new ConsoleLog());
    foreach (var m in service.GetMonitors())
        Console.WriteLine($"{m} bounds={m.Bounds} workArea={m.WorkArea} device={m.DeviceId}");
    return 0;
}

if (args.Length == 1 && args[0] == "--list-audio")
{
    // The device list the control UI will bind AudioTrack.OutputDeviceId to (the id IS the CoreAudio UID).
    foreach (var device in AudioEngine.EnumerateDevices(new ConsoleLog()))
        Console.WriteLine($"{device}  id={device.Id}");
    return 0;
}

if (!StressHarness.TryParse(args, out var options))
{
    Console.Error.WriteLine("usage: MultiMon.Stress.Mac --cycles=N --windows=N [--fullscreen] [--soak-seconds=S] [--audio [--audio-file=PATH]] [--video=CLIP [--video2=CLIP] [--hap] [--force-sw-decode] [--mode=span|individual]] | --source-check [--video=HAP.mov] [--video2=colour_h264.mp4] | --list-monitors | --list-audio");
    return 2;
}

// The main thread is AppKit's: it only pumps events (window create/show/hide land here via MainThread.Invoke).
// The harness — and the app-exit teardown — run on their own thread, never here (the V0087 deadlock rule).
var app = NSApplication.SharedApplication;
app.ActivationPolicy = NSApplicationActivationPolicy.Regular;
app.FinishLaunching();

var exitCode = 1;
var finished = new ManualResetEventSlim(false);
var harness = new Thread(() =>
{
    try { exitCode = StressHarness.Run(options, new ConsoleLog()); }
    catch (Exception ex) { Console.WriteLine($"[ERROR] Stress: harness crashed: {ex}"); exitCode = 1; }
    finally { finished.Set(); }
}) { Name = "MultiMon.Harness", IsBackground = false };
harness.Start();

while (!finished.IsSet)
{
    using var pool = new NSAutoreleasePool(); // drained per pump iteration, as NSApplication.Run would
    var evt = app.NextEvent(NSEventMask.AnyEvent, NSDate.FromTimeIntervalSinceNow(0.05), NSRunLoopMode.Default, true);
    if (evt is not null)
        app.SendEvent(evt);
}
harness.Join();
return exitCode;
