using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace KissakiViewer.Services;

/// <summary>
/// Resolves which RDB/RDX pair to load for a Kashira-mod-manager install.
///
/// Kashira patches the LIVE rdb (to point at mod fdata) and backs up the original to
/// <c>_Kashira/backup/</c>. To browse vanilla assets we normally load the backup. But if the
/// game was UPDATED (via Steam) after the last patch, the update overwrites both the live rdb and
/// the fdata — leaving the backup STALE: its recorded offsets/sizes no longer match the fdata on
/// disk, which breaks extraction of any moved/resized entry (e.g. the singleton DBs).
///
/// <c>_Kashira/rdbpatch.json</c> records, per database, the size+hash the live rdb had right after
/// the last patch (<c>RdbSize</c>/<c>RdbHash</c>). Decision:
///   • live rdb still matches that  → patch intact, backup is valid → use BACKUP (vanilla view)
///   • live rdb changed since patch → game updated / patch restored → use LIVE (matches fdata)
///   • db not listed in the json    → not patched → use LIVE (already vanilla, matches fdata)
///   • no json to reason with       → fall back to legacy behavior (use BACKUP if present)
/// </summary>
public static class KashiraBackup
{
    public static (string Rdb, string Rdx) Resolve(string gameDir, string liveRdb, string liveRdx)
    {
        string backupDir = Path.Combine(gameDir, "_Kashira", "backup");
        string rdbName   = Path.GetFileName(liveRdb);
        string backupRdb = Path.Combine(backupDir, rdbName);
        string backupRdx = Path.Combine(backupDir, Path.ChangeExtension(rdbName, ".rdx"));

        if (!File.Exists(backupRdb))
            return (liveRdb, liveRdx); // no Kashira backup for this db → live

        string db = Path.GetFileNameWithoutExtension(rdbName); // "root" / "system"
        PatchEntry? entry = TryGetPatchEntry(gameDir, db, out bool jsonPresent);

        if (jsonPresent)
        {
            // The json is authoritative about what is currently patched.
            bool intact = entry != null && PatchIntact(liveRdb, entry);
            if (!intact)
            {
                AppLogger.Info(
                    $"[RDB] Kashira: {rdbName} not actively patched (game updated / restored) — using live RDB");
                return (liveRdb, liveRdx);
            }
        }

        // Patch intact, or no json to reason with → use backup (vanilla / legacy behavior).
        string rdx = File.Exists(backupRdx) ? backupRdx : liveRdx;
        AppLogger.Info($"[RDB] Kashira backup detected, using backup of {rdbName}");
        return (backupRdb, rdx);
    }

    private static bool PatchIntact(string liveRdb, PatchEntry entry)
    {
        try
        {
            if (string.IsNullOrEmpty(entry.RdbHash)) return false;
            var fi = new FileInfo(liveRdb);
            if (entry.RdbSize > 0 && fi.Length != entry.RdbSize) return false; // cheap reject
            return string.Equals(Sha256Hex(liveRdb), entry.RdbHash, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static PatchEntry? TryGetPatchEntry(string gameDir, string db, out bool jsonPresent)
    {
        jsonPresent = false;
        string path = Path.Combine(gameDir, "_Kashira", "rdbpatch.json");
        if (!File.Exists(path)) return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            jsonPresent = true;
            if (!doc.RootElement.TryGetProperty("Databases", out var dbs) ||
                dbs.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var d in dbs.EnumerateArray())
            {
                if (d.TryGetProperty("Db", out var dbName) &&
                    string.Equals(dbName.GetString(), db, StringComparison.OrdinalIgnoreCase))
                {
                    long size  = d.TryGetProperty("RdbSize", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                    string hash = d.TryGetProperty("RdbHash", out var h) ? h.GetString() ?? "" : "";
                    return new PatchEntry(size, hash);
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"[RDB] rdbpatch.json parse failed: {ex.Message}");
        }
        return null;
    }

    private static string Sha256Hex(string path)
    {
        using var fs  = File.OpenRead(path);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)); // uppercase hex, matches rdbpatch.json
    }

    private sealed record PatchEntry(long RdbSize, string RdbHash);
}
