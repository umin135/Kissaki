using System.IO;
using KissakiViewer.Models;

namespace KissakiViewer.Services;

public static class AppSettingsService
{
    private static readonly string IniPath =
        Path.Combine(AppContext.BaseDirectory, "kissaki.ini");

    public static string AppVersion { get; } =
        typeof(AppSettingsService).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";

    public static string DefaultExportDirectory =>
        Path.Combine(AppContext.BaseDirectory, "export");

    /// <summary>Currently active settings. Updated by Save().</summary>
    public static AppSettings Current { get; private set; } = Load();

    public static AppSettings Load()
    {
        var s = new AppSettings();
        if (!File.Exists(IniPath)) return s;
        try
        {
            foreach (string raw in File.ReadLines(IniPath))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '[' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line[..eq].Trim();
                string val = line[(eq + 1)..].Trim();
                if (key.Equals("ExportDirectory", StringComparison.OrdinalIgnoreCase))
                    s.ExportDirectory = val;
            }
        }
        catch { /* non-critical */ }
        return s;
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Current = settings;
            File.WriteAllText(IniPath,
                $"[General]\r\nExportDirectory={settings.ExportDirectory}\r\n");
        }
        catch { /* non-critical */ }
    }

    /// <summary>Resolved export root (empty ExportDirectory falls back to default).</summary>
    public static string GetEffectiveExportDirectory()
        => string.IsNullOrWhiteSpace(Current.ExportDirectory)
            ? DefaultExportDirectory
            : Current.ExportDirectory;

    /// <summary>All cache directories managed by Kissaki.</summary>
    public static string[] GetCacheDirectories() =>
    [
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KissakiViewer", "cache"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KissakiViewer", "cache"),
    ];
}
