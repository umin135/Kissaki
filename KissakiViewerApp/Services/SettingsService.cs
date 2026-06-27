using System.IO;
using System.Text.Json;
using KissakiViewer.Models;

namespace KissakiViewer.Services;

public static class SettingsService
{
    private static readonly string s_dir  = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "KissakiViewer");
    private static readonly string s_path = Path.Combine(s_dir, "games.json");

    private static readonly JsonSerializerOptions s_opts = new()
    {
        WriteIndented      = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static KissakiSettings Load()
    {
        try
        {
            if (!File.Exists(s_path)) return new KissakiSettings();
            string json = File.ReadAllText(s_path);
            return JsonSerializer.Deserialize<KissakiSettings>(json, s_opts) ?? new KissakiSettings();
        }
        catch
        {
            return new KissakiSettings();
        }
    }

    public static void Save(KissakiSettings settings)
    {
        try
        {
            Directory.CreateDirectory(s_dir);
            File.WriteAllText(s_path, JsonSerializer.Serialize(settings, s_opts));
        }
        catch { /* non-critical */ }
    }
}
