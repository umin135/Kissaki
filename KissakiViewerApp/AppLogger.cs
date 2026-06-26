using System.IO;

namespace KissakiViewer;

/// <summary>
/// Thread-safe append logger. Log file: %LOCALAPPDATA%\KissakiViewer\kissaki.log
/// </summary>
public static class AppLogger
{
    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KissakiViewer", "kissaki.log");

    private static readonly object _lock = new();

    static AppLogger()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            Append($"\n========== Session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==========\n");
        }
        catch { }
    }

    public static void Info(string msg)  => Write("INFO", msg);
    public static void Warn(string msg)  => Write("WARN", msg);
    public static void Error(string msg) => Write("ERR ", msg);

    public static void Exception(string context, Exception ex) =>
        Write("EXC ", $"{context} → {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string msg) =>
        Append($"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {msg}\n");

    private static void Append(string text)
    {
        lock (_lock)
        {
            try { File.AppendAllText(LogPath, text); }
            catch { }
        }
    }
}
