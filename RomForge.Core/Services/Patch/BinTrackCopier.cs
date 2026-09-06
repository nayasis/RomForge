using CHD.Core.Services;
using Common;
using System.IO;
using System.Text.RegularExpressions;

namespace RomForge.Core.Services.Patch;

public class BinTrackCopier(Action<string, LogLevel> log)
{
    public async Task<string?> CopyBinTracksAsync(string sourcePath, string outputDir, string outputPath, List<string> copiedTrackPaths, bool moveInsteadOfCopy = false)
    {
        string[] cueCandidates = Directory.GetFiles(Path.GetDirectoryName(sourcePath)!, "*.cue");

        string? cuePath = null;
        IReadOnlyList<string>? referencedBins = null;

        foreach (var candidate in cueCandidates)
        {
            var bins = ConversionSource.ParseBinsFromCue(candidate);

            if (bins.Any(b => string.Equals(Path.GetFileName(b), Path.GetFileName(sourcePath), StringComparison.OrdinalIgnoreCase)))
            {
                cuePath = candidate;
                referencedBins = bins;

                break;
            }
        }

        if (cuePath is null || referencedBins is null)
        {
            const string message = "CUE 파일을 찾을 수 없습니다. CD 이미지가 아니거나 CUE가 누락되었을 수 있습니다.";
            log(message, LogLevel.Error);
            throw new InvalidOperationException(message);
        }

        var sourceDir = Path.GetDirectoryName(cuePath)!;
        string sourceMainFileName = Path.GetFileName(sourcePath);

        foreach (var binName in referencedBins)
        {
            if (string.Equals(Path.GetFileName(binName), sourceMainFileName, StringComparison.OrdinalIgnoreCase))
                continue;

            string sourceBinPath = Path.Combine(sourceDir, Path.GetFileName(binName));
            string targetBinPath = Path.Combine(outputDir, Path.GetFileName(binName));

            if (File.Exists(sourceBinPath))
            {
                if (moveInsteadOfCopy)
                    File.Move(sourceBinPath, targetBinPath, true);
                else
                    File.Copy(sourceBinPath, targetBinPath, true);

                copiedTrackPaths.Add(targetBinPath);
            }
            else
            {
                log($"멀티 트랙 파일을 찾을 수 없습니다: {Path.GetFileName(sourceBinPath)}", LogLevel.Error);

                return null;
            }
        }

        string newBinFileName = Path.GetFileName(outputPath);
        string outputCuePath = Path.Combine(outputDir, Path.ChangeExtension(newBinFileName, ".cue"));

        try
        {
            string cueContent = await File.ReadAllTextAsync(cuePath).ConfigureAwait(false);
            string updatedCueContent = Regex.Replace(cueContent, @"FILE\s+""([^""]+)""\s+BINARY", m =>
            {
                string referencedFileName = Path.GetFileName(m.Groups[1].Value);

                return string.Equals(referencedFileName, sourceMainFileName, StringComparison.OrdinalIgnoreCase)
                    ? $"FILE \"{newBinFileName}\" BINARY"
                    : m.Value;
            }, RegexOptions.IgnoreCase);

            await File.WriteAllTextAsync(outputCuePath, updatedCueContent).ConfigureAwait(false);

            return outputCuePath;
        }
        catch (Exception ex)
        {
            log($"CUE 파일 처리 중 오류 발생: {ex.Message}", LogLevel.Error);

            return null;
        }
    }
}
