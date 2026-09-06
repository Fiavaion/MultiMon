using AppKit;
using MultiMon.Core.Diagnostics;
using MultiMon.Platform.Mac;

// NSScreen needs the AppKit application object to exist before any display is enumerated.
NSApplication.Init();

if (args.Length == 1 && args[0] == "--list-monitors")
{
    using var service = new MonitorService(new ConsoleLog());
    foreach (var m in service.GetMonitors())
        Console.WriteLine($"{m} bounds={m.Bounds} workArea={m.WorkArea} device={m.DeviceId}");
    return 0;
}

Console.Error.WriteLine("usage: MultiMon.Stress.Mac --list-monitors");
return 2;
