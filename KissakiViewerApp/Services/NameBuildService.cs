using KissakiViewer.Core;
using KissakiViewer.Core.Formats;
using KissakiViewer.ViewModels;

namespace KissakiViewer.Services;

/// <summary>
/// .name 파일(TypeKtid=0xBF6B52C7)을 RDB에서 스캔·해제하고
/// IRNK 엔트리로부터 KTID→경로 사전을 구축한다.
///
/// .name 컨테이너는 서버사이드 온디맨드 콘텐츠일 수 있어 로컬에 없는 경우가 일반적.
/// 없는 컨테이너는 컨테이너 단위로 한 번만 경고하고 건너뛴다.
/// </summary>
public static class NameBuildService
{
    private const uint NameTypeKtid = 0xBF6B52C7u;

    public static Dictionary<uint, string> Build(
        IReadOnlyList<AssetItemViewModel> assets,
        FdataExtractor extractor,
        Action<int>? progress = null)
    {
        var result = new Dictionary<uint, string>();

        var nameAssets = assets
            .Where(a => a.Record.TypeKtid == NameTypeKtid)
            .ToList();

        if (nameAssets.Count == 0)
        {
            AppLogger.Info("[NameBuild] .name 에셋 없음");
            return result;
        }

        // 컨테이너 단위로 존재 여부를 먼저 확인
        var availableContainers = nameAssets
            .Select(a => a.Container)
            .Distinct()
            .Where(c => extractor.ContainerExists(c))
            .ToHashSet();

        var missingContainers = nameAssets
            .Select(a => a.Container)
            .Distinct()
            .Where(c => !availableContainers.Contains(c))
            .ToList();

        foreach (var c in missingContainers)
        {
            int cnt = nameAssets.Count(a => a.Container == c);
            AppLogger.Info($"[NameBuild] 컨테이너 없음 (로컬 미설치): {c} ({cnt}개 .name 에셋 건너뜀)");
        }

        var available = nameAssets.Where(a => availableContainers.Contains(a.Container)).ToList();

        if (available.Count == 0)
        {
            AppLogger.Info($"[NameBuild] 사용 가능한 .name 컨테이너 없음 — 이름 복구 불가");
            return result;
        }

        AppLogger.Info($"[NameBuild] {available.Count}개 .name 에셋 스캔 시작 ({availableContainers.Count}개 컨테이너)");

        int done = 0;
        foreach (var vm in available)
        {
            try
            {
                byte[] data = extractor.ExtractToMemory(vm.Record, vm.Container);
                if (data.Length > 0)
                {
                    foreach (var entry in NameDbParser.Parse(data))
                    {
                        foreach (string ext in NameDbParser.ExtensionsForTypeInfo(entry.TypeInfo))
                        {
                            string filePath = entry.InnerName.Contains('.')
                                ? entry.InnerName
                                : entry.InnerName + ext;

                            uint ktid = KtidHasher.ComputeForFile(filePath);
                            if (ktid != 0)
                                result.TryAdd(ktid, filePath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"[NameBuild] 0x{vm.Record.FileKtid:X8} 처리 실패: {ex.Message}");
            }

            done++;
            progress?.Invoke(done * 100 / available.Count);
        }

        AppLogger.Info($"[NameBuild] 완료: {result.Count}개 이름 추출");
        return result;
    }
}
