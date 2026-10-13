using CHD.Core.Services;
using Common;
using Patch.Core;
using System.IO;

namespace RomForge.Core.Services.Patch;

public sealed record MultiDiscPatchPair(string SourcePath, string PatchPath, string PreparedOutputPath);

public sealed record SingleDiscPatchSet(string CuePath, IReadOnlyList<MultiDiscPatchPair> DiscPairs, IReadOnlyList<MultiDiscPatchPair> AuxiliaryPairs)
{
    public IReadOnlyList<MultiDiscPatchPair> Pairs => [.. DiscPairs, .. AuxiliaryPairs];
}

public sealed record DiscPatchSet(string CuePath, IReadOnlyList<MultiDiscPatchPair> Pairs);

public sealed record MultiDiscPatchMode(IReadOnlyList<string> SourcePaths, IReadOnlyList<string> PatchPaths)
{
    public bool IsMultiDisc => SourcePaths.Count > 1 && PatchPaths.Count > 1;
}

public sealed class MultiDiscPatchResolver(Action<string, LogLevel> log)
{
    private static readonly HashSet<string> PatchExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ips", ".ips32", ".bps", ".ups", ".ppf", ".aps", ".xdelta", ".xdelta3", ".dcp"
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

    public async Task<IReadOnlyList<MultiDiscPatchPair>> PreparePatchSetAsync(string sourceDirectory, string patchDirectory, string workDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"원본 디렉토리를 찾을 수 없습니다: {sourceDirectory}");

        if (!Directory.Exists(patchDirectory))
            throw new DirectoryNotFoundException($"패치 디렉토리를 찾을 수 없습니다: {patchDirectory}");

        var sourcePaths = FindPatchableSourcePaths(sourceDirectory);
        var patchPaths = await FindUsablePatchPathsAsync(patchDirectory, ct);

        if (sourcePaths.Count == 0 || patchPaths.Count == 0)
            return [];

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
            log($"다중 파일 패치 매칭: {Path.GetFileName(matchedPair.SourcePath)} ← {Path.GetFileName(matchedPair.PatchPath)}", LogLevel.Ok);
        }

        return pairs;
    }

    public static string? FindSingleCueSourceDirectory(string sourceDirectory)
    {
        var cueDirectories = DirectoryPatchSourceResolver.EnumerateSourceDirectories(sourceDirectory)
            .Where(directory => Directory.EnumerateFiles(directory, "*.cue", SearchOption.TopDirectoryOnly).Count() == 1)
            .ToList();

        return cueDirectories.Count == 1 ? cueDirectories[0] : null;
    }

    public static SingleDiscPatchSet? TryCreateSingleDiscPatchSet(IEnumerable<MultiDiscPatchPair> pairs)
    {
        var allPairs = pairs.ToList();
        var discPairs = allPairs
            .Select(pair => (Pair: pair, CuePath: FindContainingCue(pair.SourcePath)))
            .Where(item => item.CuePath is not null)
            .ToList();

        var cuePaths = discPairs
            .Select(item => item.CuePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cuePaths.Count != 1)
            return null;

        string cuePath = cuePaths[0];
        var cuePairPaths = discPairs.Select(item => item.Pair).ToHashSet();

        return new SingleDiscPatchSet(
            cuePath,
            discPairs.Select(item => item.Pair).ToList(),
            allPairs.Where(pair => !cuePairPaths.Contains(pair)).ToList());
    }

    public static IReadOnlyList<DiscPatchSet> CreateDiscPatchSets(IEnumerable<MultiDiscPatchPair> pairs) =>
        pairs
            .Select(pair => (Pair: pair, CuePath: FindContainingCue(pair.SourcePath)))
            .Where(item => item.CuePath is not null)
            .GroupBy(item => item.CuePath!, StringComparer.OrdinalIgnoreCase)
            .Select(group => new DiscPatchSet(group.Key, group.Select(item => item.Pair).ToList()))
            .ToList();

    public static IReadOnlyList<string> FindCuePaths(string sourceDirectory) =>
        Directory.EnumerateFiles(sourceDirectory, "*.cue", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToList();

    public async Task<MultiDiscPatchMode> AnalyzeAsync(string sourceDirectory, string patchDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(sourceDirectory))
            throw new DirectoryNotFoundException($"원본 디렉토리를 찾을 수 없습니다: {sourceDirectory}");

        if (!Directory.Exists(patchDirectory))
            throw new DirectoryNotFoundException($"패치 디렉토리를 찾을 수 없습니다: {patchDirectory}");

        return new MultiDiscPatchMode(
            FindPrimarySourcePaths(sourceDirectory),
            await FindSupportedPatchPathsAsync(patchDirectory, ct));
    }

    public async Task<IReadOnlyList<string>> GetSupportedPatchPathsAsync(string patchDirectory, CancellationToken ct)
    {
        if (!Directory.Exists(patchDirectory))
            throw new DirectoryNotFoundException($"패치 디렉토리를 찾을 수 없습니다: {patchDirectory}");

        return await FindSupportedPatchPathsAsync(patchDirectory, ct);
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

    private static List<string> FindPatchableSourcePaths(string sourceDirectory) =>
        DirectoryPatchSourceResolver.EnumerateSourceDirectories(sourceDirectory)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            .Where(path => !IgnoredSourceExtensions.Contains(Path.GetExtension(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? FindContainingCue(string sourcePath)
    {
        string sourceDirectory = Path.GetDirectoryName(sourcePath)!;
        string fullSourcePath = Path.GetFullPath(sourcePath);

        foreach (string cuePath in Directory.EnumerateFiles(sourceDirectory, "*.cue", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(Path.GetFullPath(cuePath), fullSourcePath, StringComparison.OrdinalIgnoreCase)
                || ConversionSource.ParseBinsFromCue(cuePath)
                    .Any(binPath => string.Equals(Path.GetFullPath(binPath), fullSourcePath, StringComparison.OrdinalIgnoreCase)))
                return Path.GetFullPath(cuePath);
        }

        return null;
    }

    private static async Task<List<string>> FindUsablePatchPathsAsync(string patchDirectory, CancellationToken ct)
    {
        var paths = new List<string>();

        foreach (var patchPath in await FindSupportedPatchPathsAsync(patchDirectory, ct))
        {
            if (await PatchSourceValidation.SupportsDirectCompressedSourceAsync(patchPath, ct))
                paths.Add(patchPath);
        }

        return paths;
    }

    private static Task<List<string>> FindSupportedPatchPathsAsync(string patchDirectory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        return Task.FromResult(Directory.EnumerateFiles(patchDirectory, "*", SearchOption.AllDirectories)
            .Where(path => PatchExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => GetPatchPriority(Path.GetExtension(path)))
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList());
    }

    private static int GetPatchPriority(string extension) => extension.ToLowerInvariant() switch
    {
        ".xdelta" or ".xdelta3" => 0,
        ".bps" => 1,
        ".ppf" => 2,
        ".ups" => 3,
        ".ips" or ".ips32" => 4,
        ".dcp" => 5,
        ".aps" => 6,
        _ => int.MaxValue
    };

    private static IOrderedEnumerable<string> OrderCandidates(IEnumerable<string> sourcePaths, string patchPath, ISet<string> usedSources)
    {
        string patchName = Path.GetFileNameWithoutExtension(patchPath);

        return sourcePaths
            .Where(path => !usedSources.Contains(path))
            .OrderBy(path => string.Equals(Path.GetFileNameWithoutExtension(path), patchName, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
    }
}
