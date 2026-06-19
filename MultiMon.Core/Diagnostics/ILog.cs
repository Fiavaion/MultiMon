namespace MultiMon.Core.Diagnostics;

/// <summary>Minimal logging sink. <see cref="ConsoleLog"/> backs the console-attached harness;
/// <see cref="FileLog"/> backs the windowed app (no console) with a <c>%LOCALAPPDATA%\MultiMon\Logs\</c>
/// file sink that also echoes to the console.</summary>
public interface ILog
{
    void Info(string source, string message);
    void Debug(string source, string message);
    void Error(string source, string message);
}

/// <summary>Console logger for console-attached processes (the stress harness, or a redirected launch).
/// The windowed app uses <see cref="FileLog"/> instead, since a windowed launch has no console.</summary>
public sealed class ConsoleLog : ILog
{
    public void Info(string source, string message)  => Console.WriteLine($"[INFO ] {source}: {message}");
    public void Debug(string source, string message) => Console.WriteLine($"[DEBUG] {source}: {message}");
    public void Error(string source, string message) => Console.WriteLine($"[ERROR] {source}: {message}");
}
