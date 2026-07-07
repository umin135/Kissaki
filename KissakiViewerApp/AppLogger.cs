using System.IO;
using System.Threading.Channels;

namespace KissakiViewer;

/// <summary>
/// Thread-safe append logger. Log file: %LOCALAPPDATA%\KissakiViewer\kissaki.log
/// File writes are asynchronous (background channel writer) so the UI thread
/// is never blocked by slow file I/O (e.g. antivirus scan on write).
/// </summary>
public static class AppLogger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KissakiViewer", "kissaki.log");

    private static readonly Channel<string> _channel =
        Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false,
        });

    /// <summary>Fired synchronously on the calling thread whenever a line is written.</summary>
    public static event Action<string>? LogAdded;

    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            // Write session header synchronously so it always lands first.
            File.WriteAllText(LogPath, $"========== Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n");
        }
        catch { }

        // Background writer: drains the channel and writes to disk sequentially.
        _ = Task.Run(async () =>
        {
            await foreach (var text in _channel.Reader.ReadAllAsync())
            {
                try { File.AppendAllText(LogPath, text); }
                catch { }
            }
        });
    }

    public static void Info(string msg)  => Write("INFO", msg);
    public static void Warn(string msg)  => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Exception(string context, Exception ex) =>
        Write("EXC ", $"{context} → {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}";
        // Non-blocking enqueue — background thread does the actual file write.
        _channel.Writer.TryWrite(line + "\n");
        try { LogAdded?.Invoke(line); } catch { }
    }
}
