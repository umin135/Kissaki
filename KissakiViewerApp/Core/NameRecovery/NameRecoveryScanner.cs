using System.IO;
using System.Text;

namespace KissakiViewer.Core.NameRecovery;

/// <summary>
/// Scans binary files (game exe, fdata, etc.) for ASCII strings, hashes them
/// with the KatanaEngine KTID hash function, and matches against known FileKtid values.
/// Inspired by Ninja Gaiden Sigma .rdata scan and RDBExplorer NameGrabber approach.
/// </summary>
public static class NameRecoveryScanner
{
    private const int MinStringLength = 6;

    /// <summary>
    /// Scans a binary file for ASCII strings that hash to a known FileKtid.
    /// Returns a map: FileKtid → recovered path string.
    /// Also appends a discovery log to the provided list.
    /// </summary>
    public static Dictionary<uint, string> Scan(
        string filePath,
        HashSet<uint> knownKtids,
        IList<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new Dictionary<uint, string>();
        if (!File.Exists(filePath)) return results;

        byte[] data = File.ReadAllBytes(filePath);
        log?.Add($"Scan: {filePath}  ({data.Length / 1024.0 / 1024.0:F1} MB)");

        int matched = 0;
        int stringsChecked = 0;
        int i = 0;
        int lastPct = -1;

        while (i < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (progress != null)
            {
                int pct = (int)((long)i * 100 / data.Length);
                if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
            }

            // Advance to next printable-ASCII run
            if (!IsPrintable(data[i])) { i++; continue; }

            int start = i;
            while (i < data.Length && IsPrintable(data[i])) i++;
            int len = i - start;

            if (len < MinStringLength) continue;

            // Try every substring starting at start (length MinStringLength..len)
            // to catch strings that are embedded without null terminators
            for (int slen = MinStringLength; slen <= len; slen++)
            {
                string s = Encoding.ASCII.GetString(data, start, slen);
                stringsChecked++;

                uint h = KtidHasher.Compute(s);
                if (knownKtids.Contains(h) && !results.ContainsKey(h))
                {
                    results[h] = s;
                    matched++;
                    log?.Add($"  MATCH  0x{h:x8} = \"{s}\"");
                }
            }
        }

        log?.Add($"Scan complete: {stringsChecked:N0} strings checked, {matched} KTID matches found.");
        return results;
    }

    /// <summary>
    /// Lightweight mode: only try null-terminated strings (faster for large exe files).
    /// </summary>
    public static Dictionary<uint, string> ScanNullTerminated(
        string filePath,
        HashSet<uint> knownKtids,
        IList<string>? log = null,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        var results = new Dictionary<uint, string>();
        if (!File.Exists(filePath)) return results;

        byte[] data = File.ReadAllBytes(filePath);
        log?.Add($"Scan (NUL mode): {filePath}  ({data.Length / 1024.0 / 1024.0:F1} MB)");

        int matched = 0;
        int stringsChecked = 0;
        int i = 0;
        int lastPct = -1;
        var sb = new StringBuilder(256);

        while (i < data.Length)
        {
            ct.ThrowIfCancellationRequested();

            if (progress != null)
            {
                int pct = (int)((long)i * 100 / data.Length);
                if (pct != lastPct) { progress.Report(pct); lastPct = pct; }
            }

            if (data[i] == 0)
            {
                // End of a null-terminated string
                if (sb.Length >= MinStringLength)
                {
                    string s = sb.ToString();
                    stringsChecked++;
                    uint h = KtidHasher.Compute(s);
                    if (knownKtids.Contains(h) && !results.ContainsKey(h))
                    {
                        results[h] = s;
                        matched++;
                        log?.Add($"  MATCH  0x{h:x8} = \"{s}\"");
                    }
                }
                sb.Clear();
            }
            else if (IsPrintable(data[i]))
            {
                sb.Append((char)data[i]);
            }
            else
            {
                sb.Clear();
            }

            i++;
        }

        log?.Add($"Scan complete: {stringsChecked:N0} strings checked, {matched} KTID matches found.");
        return results;
    }

    /// <summary>
    /// Brute-force using the small_dictionary from eterniti/eternity_common/DOA6/small_dictionary.h.
    /// Generates candidate paths from known path components and hashes them.
    /// </summary>
    public static Dictionary<uint, string> BruteForce(
        HashSet<uint> knownKtids,
        IList<string>? log = null,
        CancellationToken ct = default)
    {
        // Official char codes from eterniti/eternity_common/DOA6/small_dictionary.h
        // Playable: KAS=Kasumi, BAS=Bass, BAY=Bayman, JAN=Jann Lee, LEI=Lei Fang, HTM=Hitomi,
        //   RYU=Ryu, HYT=Hayate, MAR=Marie Rose, NIC=NiCO, KOK=Kokoro, NYO=Nyotengu,
        //   RID=Raidou, MIL=Mila, ZAC=Zack, TIN=Tamaki, HEL=Helena, AYA=Ayane, ELI=Eliot,
        //   LIS=Lisa, BRA=Brad, CRI=Christie, RIG=Rig, HON=Honoka, DGO=Diego, PHF=Phase4,
        //   MAI=Mai, SNK=Naotora, MOM=Momiji, RAC=Rachel, SKD=unknown DLC
        // Non-playable: CMN, ARD, MPP, SRD
        string[] charCodes = [
            "KAS", "BAS", "BAY", "JAN", "LEI", "HTM", "RYU", "HYT",
            "MAR", "NIC", "KOK", "NYO", "RID", "MIL", "ZAC", "TIN",
            "HEL", "AYA", "ELI", "LIS", "BRA", "CRI", "RIG", "HON",
            "DGO", "PHF", "MAI", "SNK", "MOM", "RAC", "SKD",
            "CMN", "ARD", "MPP", "SRD",
        ];

        // Official texture suffixes from small_dictionary.h texture_types array
        string[] texTypes = [
            "kidsair", "kidsalb", "kidsnmh", "kidsocc",
            "kidsrfr", "kidswtm", "kidsmm1", "kidsmm2",
            "kidsshl", "kidss4m", "kidsalem", "kidsemi",
            "kidsthk", "kidsnmb", "kidsfur", "kidsofs",
        ];

        // Body-part vocabulary from small_dictionary.h (subset relevant to character textures)
        string[] parts = [
            // Indexed costume slots
            "a01","a02","a03","a04","a05",
            "b01","b02","b03","b04","b05",
            "c01","c02","c03","c04","c05",
            "d01","d02","d03","d04","d05",
            "e01","e02","e03","e04","e05",
            "f01","f02","f03","f04","f05",
            "g01","g02","g03","g04","g05",
            "h01","h02","h03","h04","h05",
            "i01","i02","i03","i04",
            "j01","j02","j03","j04",
            "k01","k02","k03","k04",
            "l01","l02","l03","l04",
            // Sub-slots (xNN_NN)
            "a01_01","a01_02","a01_03","a01_04",
            "a02_01","a02_02","a02_03","a02_04",
            "b01_01","b01_02","b01_03","b01_04",
            "b02_01","b02_02","b02_03","b02_04",
            "c01_01","c01_02","c01_03","c01_04",
            "d01_01","d01_02","d01_03","d01_04",
            // Common body parts
            "body","body01","body02","body03","body04",
            "face","face2","eye","eye01","eye02","matuge","tooth","es",
            "hair","hair01","hair02","hair03","hair04","hair1","hair2","hair3","hair4",
            "armour","helmet","weapon","gun","katana",
            "ribbon","ribbon01","ribbon02","ribbon03","ribbon04","hairtie","harigane",
            "button","beard01","cap","cap01","cap02","cap03","cap04",
            "glove","glove01","glove02","glove03","glove04",
            "pants","pants01","pants02","pants03","pants04",
            "accessory","ornament","ornament1","ornament01",
            "fur01","fur02","fur03","fur04",
            "cloth","cloth01","cloth02","cloth03","cloth04",
            "mant","mant01","mant02","mant03","mant04",
            "other","other01","other02","other03","other04",
            "gem","lace","band","hachimaki","hakama","helmet",
            "skirt","skirt01","bikini","bikini01","swimsuit","swimsuit01",
            "blend","BLEND","blend02","blend03","blend04",
            "etc","etc01","etc02","line","line01","line02","line03",
            "hip","muna","pbody","pbody02","pbody03",
            "kanzashi","hairpin","hairpin2","fusa","fusa01",
            "sensu","sensu01","sensu02","sensu03","fan","fan01","fan02","fan03",
            "bouquet","bouquet01","flower","flower1","flower2","scroll",
            "mask01","mask02","mask03","bell","bell01","bell1",
            "candy","candy01","candy02","snkcandy","snkcandy_1",
            "bunny","bunny01","ear","costume","costume01",
            "dragon","dragon01","christmas","christmas01",
            "extension","fringe","fringe01","bangs","bangs01",
            "lock","lock01","weight","dumbbell",
            "particle","particle01","effect","effect01","glow","glow01",
            "z","body_z","bodyz","body_x","wbody","pbody",
            "mob0body","mob0face","mob0cloth","mob0head","mobbody",
        ];

        string[] prefixes = [
            "CE1ResourceStaticTexture",
            "CE1ResourceDynamicTexture",
            "ME1ResourceStaticTexture",
            "FE4ResourceStaticTexture",
        ];
        // Fullwidth brackets used as path delimiters in KatanaEngine resource names
        string open  = "［";
        string close = "］";

        var results = new Dictionary<uint, string>();
        int candidates = 0;
        int matched = 0;

        log?.Add("BruteForce: generating candidates from small_dictionary (official)...");

        // 4-digit format (game uses e.g. RYU0001) — confirmed by existing matches
        foreach (string prefix in prefixes)
        foreach (string ch in charCodes)
        for (int num = 1; num <= 50; num++)
        foreach (string part in parts)
        foreach (string tex in texTypes)
        {
            ct.ThrowIfCancellationRequested();
            TryCandidate($"{prefix}{open}MPR_Muscle_Character_{ch}{num:D4}_{part}_{tex}{close}");
            TryCandidate($"{prefix}{open}Character_{ch}{num:D4}_{part}_{tex}{close}");
        }

        // No-number variants (shared/common assets without slot index)
        foreach (string prefix in prefixes)
        foreach (string ch in charCodes)
        foreach (string part in parts)
        foreach (string tex in texTypes)
        {
            ct.ThrowIfCancellationRequested();
            TryCandidate($"{prefix}{open}MPR_Muscle_Character_{ch}_{part}_{tex}{close}");
            TryCandidate($"{prefix}{open}Character_{ch}_{part}_{tex}{close}");
        }

        log?.Add($"BruteForce complete: {candidates:N0} candidates, {matched} matches.");
        return results;

        void TryCandidate(string candidate)
        {
            candidates++;
            uint h = KtidHasher.Compute(candidate);
            if (knownKtids.Contains(h) && !results.ContainsKey(h))
            {
                results[h] = candidate;
                matched++;
                log?.Add($"  MATCH  0x{h:x8} = \"{candidate}\"");
            }
        }
    }

    private static bool IsPrintable(byte b) => b >= 0x20 && b < 0x7F;
}
