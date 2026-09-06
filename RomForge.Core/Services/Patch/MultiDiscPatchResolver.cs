using CHD.Core.Services;
using Common;
using Patch.Core;
using Patch.Core.Enums;
using System.IO;

namespace RomForge.Core.Services.Patch;

public sealed record MultiDiscPatchPair(string SourcePath, string PatchPath, string PreparedOutputPath);

public sealed class MultiDiscPatchResolver(Action<string, LogLevel> log)
{
    private static readonly HashSet<string> PatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bps", ".ups", ".ppf", ".xdelta", ".xdelta3"
    };

    private static readonly HashSet<string> IgnoredSourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cue", ".m3u", ".txt", ".log", ".nfo", ".diz", ".url", ".ini", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".md5", ".sfv", ".pdf"
    };

    public async Task<IReadOnlyList<MultiDiscPatchPair>> PrepareAsync(string sourceDirectory, string patchDirectory, string workDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"원본 디렉토리를 찾을 수 없습니다: {sourceDirectory}");

        if (!Directory.Exists(patchDirectory))
            throw new DirectoryNotFoundException($"패치 디렉토리를 찾을 수 없습니다: {patchDirectory}");

        var sourcePaths = FindPrimarySourcePaths(sourceDirectory);
        if (sourcePaths.Count == 0)
            throw new InvalidOperationException("멀티 디스크 패치에 사용할 원본 파일을 찾을 수 없습니다.");

        var patchPaths = await FindUsablePatchPathsAsync(patchDirectory, ct);
        if (patchPaths.Count == 0)
            throw new InvalidOperationException("체크섬 또는 원본 확인을 지원하는 패치 파일이 없습니다. BPS, UPS, 체크 블록이 있는 PPF, Xdelta만 사용할 수 있습니다.");

        Directory.CreateDirectory(workDirectory);

        var pairs = new List<MultiDiscPatchPair>();
        var usedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var patchPath in patchPaths)
        {
            MultiDiscPatchPair? matchedPair = null;

            foreach (var sourcePath in OrderCandidates(sourcePaths, patchPath, usedSources))
            {
                ct.ThrowIfCancellationRequested();

                string preparedOutputPath = Path.Combine(workDirectory, $"{Guid.NewGuid():N}{Path.GetExtension(sourcePath)}");

                try
                {
                    await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, preparedOutputPath, null, ct);
                    matchedPair = new MultiDiscPatchPair(sourcePath, patchPath, preparedOutputPath);
                    break;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    if (File.Exists(preparedOutputPath))
                        File.Delete(preparedOutputPath);
                }
            }

            if (matchedPair is null)
            {
                log($"원본을 확인하지 못해 제외한 패치: {Path.GetFileName(patchPath)}", LogLevel.Error);
                continue;
            }

            pairs.Add(matchedPair);
            usedSources.Add(matchedPair.SourcePath);
            log($"멀티 디스크 패치 매칭: {Path.GetFileName(matchedPair.SourcePath)} ← {Path.GetFileName(matchedPair.PatchPath)}", LogLevel.Ok);
        }

        if (pairs.Count == 0)
            throw new InvalidOperationException("원본과 일치하는 멀티 디스크 패치를 찾을 수 없습니다.");

        var unmatchedSources = sourcePaths.Where(path => !usedSources.Contains(path)).ToList();
        if (unmatchedSources.Count > 0)
        {
            foreach (var pair in pairs)
                if (File.Exists(pair.PreparedOutputPath))
                    File.Delete(pair.PreparedOutputPath);

            throw new InvalidOperationException($"패치가 없는 디스크 원본이 있습니다: {string.Join(", ", unmatchedSources.Select(Path.GetFileName))}");
        }

        return pairs;
    }

    private static List<string> FindPrimarySourcePaths(string sourceDirectory)
    {
        var companionBins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var primaryPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cuePath in Directory.EnumerateFiles(sourceDirectory, "*.cue", SearchOption.TopDirectoryOnly))
        {
            var bins = ConversionSource.ParseBinsFromCue(cuePath);
            int mainIndex = ConversionSource.ResolveMainDataTrackIndex(cuePath);

            foreach (var bin in bins)
                companionBins.Add(Path.GetFullPath(Path.Combine(sourceDirectory, Path.GetFileName(bin))));

            if (mainIndex >= 0 && mainIndex < bins.Count)
            {
                string mainBinPath = Path.Combine(sourceDirectory, Path.GetFileName(bins[mainIndex]));
                if (File.Exists(mainBinPath))
                    primaryPaths.Add(Path.GetFullPath(mainBinPath));
            }
        }

        foreach (var path in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            if (IgnoredSourceExtensions.Contains(Path.GetExtension(path)))
                continue;

            string fullPath = Path.GetFullPath(path);
            if (companionBins.Contains(fullPath) && !primaryPaths.Contains(fullPath))
                continue;

            primaryPaths.Add(fullPath);
        }

        return primaryPaths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<List<string>> FindUsablePatchPathsAsync(string patchDirectory, CancellationToken ct)
    {
        var paths = new List<string>();

        foreach (var patchPath in Directory.EnumerateFiles(patchDirectory, "*", SearchOption.TopDirectoryOnly)
                     .Where(path => PatchExtensions.Contains(Path.GetExtension(path)))
                     .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            PatchFormat format = await UniversalPatcher.DetectFormatAsync(patchPath, ct);

            if (format is PatchFormat.Bps or PatchFormat.Ups or PatchFormat.Xdelta || HasPpfBlockCheck(patchPath, format))
                paths.Add(patchPath);
        }

        return paths;
    }

    private static bool HasPpfBlockCheck(string patchPath, PatchFormat format)
    {
        if (format != PatchFormat.Ppf)
            return false;

        using var stream = new FileStream(patchPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var header = new byte[60];
        if (stream.Read(header, 0, header.Length) != header.Length)
            return false;

        int version = header[3] - '0';
        return version == 2 || version == 3 && header[57] != 0;
    }

    private static IOrderedEnumerable<string> OrderCandidates(IEnumerable<string> sourcePaths, string patchPath, ISet<string> usedSources)
    {
        string patchName = Path.GetFileNameWithoutExtension(patchPath);

        return sourcePaths
            .Where(path => !usedSources.Contains(path))
            .OrderBy(path => string.Equals(Path.GetFileNameWithoutExtension(path), patchName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }
}
