using System.Text;

namespace MultiMon.Core.Diagnostics;

/// <summary>
/// File-backed <see cref="ILog"/> for the windowed app, which has no console (a normal double-click launch
/// loses every <see cref="ConsoleLog"/> line — the field-diagnosability gap noted in RELEASE_REVIEW.md).
/// Writes timestamped lines to <c>%LOCALAPPDATA%\MultiMon\Logs\</c> and also echoes to the console, so a
/// redirected launch (<c>app.exe &gt; mm.log</c>) still captures everything.
///
/// <para>Logging must never destabilize the app: construction failures fall back to console-only, and every
/// write is guarded — a logging fault is swallowed, never thrown into a render/decode/audio thread.
/// Writes are serialized by a lock (all engine threads share one sink) and flushed per line so a crash
/// still leaves the tail on disk.</para>
/// </summary>
public sealed class FileLog : ILog, IDisposable
{
    private const int RetainFiles = 10;

    private readonly object _gate = new();
    private readonly StreamWriter? _writer;
    private bool _disposed;

    /// <summary>Full path of the active log file, or null if the file sink could not be opened
    /// (in which case logging continues to the console only).</summary>
    public string? FilePath { get; }

    public FileLog()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MultiMon", "Logs");
            Directory.CreateDirectory(dir);
            Prune(dir);

            FilePath = Path.Combine(dir, $"multimon-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _writer = new StreamWriter(
                new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // No file sink (e.g. locked/inaccessible LOCALAPPDATA) — degrade to console only, never throw.
            _writer = null;
            FilePath = null;
        }
    }

    public void Info(string source, string message)  => Write("INFO ", source, message);
    public void Debug(string source, string message) => Write("DEBUG", source, message);
    public void Error(string source, string message) => Write("ERROR", source, message);

    private void Write(string level, string source, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {source}: {message}";
        lock (_gate)
        {
            if (_disposed)
                return;
            try { _writer?.WriteLine(line); } catch { /* logging must not crash a worker thread */ }
        }
        try { Console.WriteLine(line); } catch { /* no console attached — harmless */ }
    }

    /// <summary>Keep the newest <see cref="RetainFiles"/> logs so the folder can't grow without bound.</summary>
    private static void Prune(string dir)
    {
        try
        {
            var stale = new DirectoryInfo(dir).GetFiles("multimon-*.log")
                .OrderByDescending(f => f.Name)
                .Skip(RetainFiles - 1);
            foreach (var f in stale)
                try { f.Delete(); } catch { /* a held file just survives this round */ }
        }
        catch { /* pruning is best-effort */ }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            try { _writer?.Dispose(); } catch { /* closing a faulted writer must not throw at exit */ }
        }
    }
}
