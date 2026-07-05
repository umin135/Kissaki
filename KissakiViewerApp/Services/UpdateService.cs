using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace KissakiViewer.Services;

public record ReleaseInfo(string TagName, string ChangeLog, string DownloadUrl);

public static class UpdateService
{
    private const string ApiUrl = "https://api.github.com/repos/umin135/Kissaki/releases/latest";

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };

    static UpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"KissakiViewer/{AppSettingsService.AppVersion}");
    }

    public static async Task<ReleaseInfo?> CheckForUpdateAsync()
    {
        AppLogger.Info($"[Update] Checking — current: v{AppSettingsService.AppVersion}  url: {ApiUrl}");
        try
        {
            var json = await _http.GetStringAsync(ApiUrl);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag  = root.GetProperty("tag_name").GetString() ?? "";
            var body = root.GetProperty("body").GetString() ?? "";

            AppLogger.Info($"[Update] Latest tag: {tag}");

            if (!Version.TryParse(tag.TrimStart('v'), out var latest))
            {
                AppLogger.Warn($"[Update] Could not parse version from tag '{tag}'");
                return null;
            }
            if (!Version.TryParse(AppSettingsService.AppVersion, out var current))
            {
                AppLogger.Warn($"[Update] Could not parse current version '{AppSettingsService.AppVersion}'");
                return null;
            }
            if (latest <= current)
            {
                AppLogger.Info($"[Update] Up to date ({latest} <= {current})");
                return null;
            }

            string? dlUrl = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? "";
                AppLogger.Info($"[Update] Asset: {name}");
                if (name.StartsWith("KissakiViewer-v") && name.EndsWith(".zip"))
                {
                    dlUrl = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }
            if (dlUrl is null)
            {
                AppLogger.Warn("[Update] No matching asset found (expected KissakiViewer-v*.zip)");
                return null;
            }

            AppLogger.Info($"[Update] Update available: {tag} → {dlUrl}");
            return new ReleaseInfo(tag, body, dlUrl);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[Update] Check failed: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    public static async Task DownloadAndApplyAsync(string downloadUrl, IProgress<int>? progress = null)
    {
        var tempDir    = Path.Combine(Path.GetTempPath(), "KissakiUpdate");
        var zipPath    = Path.Combine(tempDir, "update.zip");
        var extractDir = Path.Combine(tempDir, "extracted");

        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        // Download
        using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0L;

        await using (var src = await response.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(zipPath))
        {
            var buf      = new byte[81920];
            long received = 0;
            int  read;
            while ((read = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read));
                received += read;
                if (total > 0) progress?.Report((int)(received * 100 / total));
            }
        }
        progress?.Report(100);

        // Extract
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // If zip has a single root folder, descend into it
        var sourceDir = extractDir;
        var entries   = Directory.GetFileSystemEntries(extractDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
            sourceDir = entries[0];

        // Write PowerShell updater script
        var appDir     = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var appExe     = Environment.ProcessPath ?? Path.Combine(appDir, "KissakiViewer.exe");
        var scriptPath = Path.Combine(Path.GetTempPath(), "kissaki_update.ps1");

        var ps = new StringBuilder();
        ps.AppendLine("Start-Sleep -Seconds 2");
        ps.AppendLine($"$src = '{Esc(sourceDir)}'");
        ps.AppendLine($"$dst = '{Esc(appDir)}'");
        ps.AppendLine("Get-ChildItem -LiteralPath $src -Recurse | ForEach-Object {");
        ps.AppendLine("    $target = $_.FullName.Replace($src, $dst)");
        ps.AppendLine("    if ($_.PSIsContainer) { New-Item -ItemType Directory -Path $target -Force | Out-Null }");
        ps.AppendLine("    else { Copy-Item -LiteralPath $_.FullName -Destination $target -Force }");
        ps.AppendLine("}");
        ps.AppendLine($"Start-Process -FilePath '{Esc(appExe)}'");
        ps.AppendLine("Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force");

        File.WriteAllText(scriptPath, ps.ToString(), Encoding.UTF8);

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-ExecutionPolicy Bypass -WindowStyle Hidden -NonInteractive -File \"{scriptPath}\"",
            WindowStyle     = ProcessWindowStyle.Hidden,
            CreateNoWindow  = true,
            UseShellExecute = false
        });

        Application.Current.Shutdown();
    }

    private static string Esc(string path) => path.Replace("'", "''");
}
