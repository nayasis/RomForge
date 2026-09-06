using CHD.Core.Services;
using Common;
using System.IO;

namespace RomForge.Core.Services.Patch;

internal static class DcpChdStagingConverter
{
    public static async Task<string> ConvertAsync(string gdiPath, string outputDirectory, string compression, IProgress<ProgressInfo> progress, Action<string, LogLevel> log, CancellationToken ct)
    {
        string stagingDirectory = Path.Combine(Path.GetTempPath(), "RomForge", "dcp-chd", Guid.NewGuid().ToString("N"));
        string stagedGdiPath = Path.Combine(stagingDirectory, Path.GetFileName(gdiPath));
        string? finalChdPath = null;

        Directory.CreateDirectory(stagingDirectory);

        try
        {
            File.Copy(gdiPath, stagedGdiPath);

            string sourceDirectory = Path.GetDirectoryName(gdiPath)!;

            foreach (string trackName in ConversionSource.ParseFilesFromGdi(gdiPath))
            {
                string fileName = Path.GetFileName(trackName);
                string sourceTrackPath = Path.Combine(sourceDirectory, fileName);

                if (!File.Exists(sourceTrackPath))
                    throw new FileNotFoundException($"GDI 트랙 파일을 찾을 수 없습니다: {sourceTrackPath}", sourceTrackPath);

                File.CreateSymbolicLink(Path.Combine(stagingDirectory, fileName), sourceTrackPath);
            }

            log($"DCP CHD 변환용 단축 경로 준비: {stagedGdiPath}", LogLevel.Highlight);

            using var converter = new FileConverter(compression);
            converter.LogMessage += (_, e) => log(e.Message, e.Level);

            var result = await converter.ConvertFileAsync(stagedGdiPath, null, progress, ct);

            if (!result.Success || string.IsNullOrEmpty(result.OutputFile))
                throw new InvalidOperationException($"CHD 변환 실패: {result.Message}");

            finalChdPath = Utils.GetUniqueFilePath(Path.Combine(outputDirectory, Path.GetFileNameWithoutExtension(gdiPath) + ".chd"));

            await using var source = new FileStream(result.OutputFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
            await using var destination = new FileStream(finalChdPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
            var reporter = new ProgressReporter("CHD 결과 저장 중...", string.Empty, source.Length, progress);

            await Utils.CopyStreamAsync(source, destination, reporter.AddProgress, ct);

            log($"DCP CHD 변환 완료: {finalChdPath}", LogLevel.Ok);

            return finalChdPath;
        }
        catch
        {
            if (finalChdPath is not null && File.Exists(finalChdPath))
                File.Delete(finalChdPath);

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
                Directory.Delete(stagingDirectory, true);
        }
    }
}
