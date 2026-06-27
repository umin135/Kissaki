using System.IO;
using System.Text;
using KissakiViewer.Core.Formats;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Services;

/// <summary>
/// G1MX / G1COX / G1P 파일 헤더에 내장된 경로 문자열을 추출한다.
/// RDBExplorer NameGrabber (isGrabMagic=false) 와 동일한 로직.
///
/// 파일 헤더 레이아웃 (디코딩된 바이트 기준):
///   [+0x00] u32  magic  (예: "M1OK", "XC1G", "P1GK")
///   [+0x04] u32  version
///   [+0x08] u32  size
///   [+0x0C] i32  stringLength
///   [+0x10] u8[] ASCII path (stringLength bytes, null-padded)
/// </summary>
public static class NameGrabberService
{
    // TypeKtid → 파일 확장자
    private static readonly Dictionary<uint, string> TargetTypes = new()
    {
        [0x17614AF5u] = ".g1mx",
        [0xA8D88566u] = ".g1cox",
        [0x79C724C2u] = ".g1p",
    };

    /// <summary>
    /// 에셋 목록에서 G1MX/G1COX/G1P 파일의 내장 경로를 추출한다.
    /// 반환값: FileKtid → "path/name.ext" (확장자 포함)
    /// </summary>
    public static Dictionary<uint, string> Grab(
        IReadOnlyList<AssetItemViewModel> assets,
        FdataExtractor extractor,
        Action<int>? progress = null)
    {
        var result = new Dictionary<uint, string>();

        var targets = assets
            .Where(a => TargetTypes.ContainsKey(a.Record.TypeKtid))
            .ToList();

        if (targets.Count == 0)
        {
            AppLogger.Info("[NameGrabber] 대상 에셋 없음 (G1MX/G1COX/G1P)");
            return result;
        }

        // 존재하는 컨테이너만 처리
        var presentContainers = targets
            .Select(a => a.Container)
            .Distinct()
            .Where(c => extractor.ContainerExists(c))
            .ToHashSet();

        var available = targets.Where(a => presentContainers.Contains(a.Container)).ToList();

        AppLogger.Info($"[NameGrabber] 대상 {targets.Count}개, 접근 가능 {available.Count}개 스캔 시작");

        int done = 0;
        int found = 0;

        foreach (var vm in available)
        {
            try
            {
                byte[] data = extractor.ExtractToMemory(vm.Record, vm.Container);
                if (data.Length >= 16)
                {
                    string? path = ReadEmbeddedPath(data);
                    if (path != null)
                    {
                        string ext = TargetTypes[vm.Record.TypeKtid];
                        string fullPath = path.Contains('.') ? path : path + ext;
                        result[vm.Record.FileKtid] = fullPath;
                        found++;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[NameGrabber] 0x{vm.Record.FileKtid:X8} 처리 실패: {ex.Message}");
            }

            done++;
            progress?.Invoke(done * 100 / available.Count);
        }

        AppLogger.Info($"[NameGrabber] 완료: {found}개 이름 추출");
        return result;
    }

    /// <summary>
    /// 디코딩된 파일 바이트에서 내장 경로 문자열을 읽는다.
    /// 유효하지 않으면 null 반환.
    /// </summary>
    private static string? ReadEmbeddedPath(byte[] data)
    {
        if (data.Length < 16) return null;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.ASCII, leaveOpen: false);

        br.ReadUInt32(); // magic
        br.ReadUInt32(); // version
        br.ReadUInt32(); // size
        int strLen = br.ReadInt32(); // stringLength at +0x0C

        if (strLen <= 0 || strLen >= 1024) return null;
        if (ms.Position + strLen > data.Length) return null;

        string path = Encoding.ASCII.GetString(br.ReadBytes(strLen)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
