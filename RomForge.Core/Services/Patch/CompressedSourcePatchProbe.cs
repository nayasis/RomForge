using Common;
using Patch.Core;
using System.IO;

namespace RomForge.Core.Services.Patch;

public static class CompressedSourcePatchProbe
{
    public static async Task<bool> TryApplyAsync(string sourcePath, string patchPath, string outputPath, Action<string, LogLevel> log, IProgress<ProgressInfo> progress, CancellationToken ct)
    {
        if (!IsCompressedSource(sourcePath) || !await PatchSourceValidation.SupportsDirectCompressedSourceAsync(patchPath, ct))
            return false;

        log($"압축 원본 직접 패치 검증: {Path.GetFileName(sourcePath)}", LogLevel.Highlight);

        try
        {
            await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, outputPath, progress, ct);
            log("압축 원본 직접 패치 검증 성공", LogLevel.Ok);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            log($"압축 원본 직접 패치 실패: {ex.Message}. 압축 해제 후 재시도합니다.", LogLevel.Info);
            return false;
        }
    }

    private static bool IsCompressedSource(string path) =>
        Path.GetExtension(path).Equals(".chd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".rvz", StringComparison.OrdinalIgnoreCase);
}
