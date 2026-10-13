using CHD.Core.Services;
using Common;
using Patch.Core.Formats.DCP.Services;
using RomForge.Core.Models.Compression;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RomForge.Core.Services.Patch;

public sealed record PackageResultTarget(string TargetPath, string? ObsoletePath = null);

public class ElPackageCreator(Action<string, LogLevel> log)
{
    public static HashSet<string> CaptureOutputEntries(string outputDir) =>
        Directory.Exists(outputDir)
            ? new(Directory.EnumerateFileSystemEntries(outputDir).Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> GetNewOutputEntries(string outputDir, ISet<string> entriesBefore) =>
        Directory.Exists(outputDir)
            ? [.. Directory.EnumerateFileSystemEntries(outputDir)
                .Select(Path.GetFullPath)
                .Where(path => !entriesBefore.Contains(path))]
            : [];

    public static IReadOnlyList<string> ResolveSourcePaths(string sourcePath, RomFormat format)
    {
        var paths = new List<string> { sourcePath };
        string sourceDir = Path.GetDirectoryName(sourcePath)!;

        if (format == RomFormat.Bin)
        {
            string sourceFileName = Path.GetFileName(sourcePath);
            string? cuePath = Directory.EnumerateFiles(sourceDir, "*.cue")
                .FirstOrDefault(candidate => ConversionSource.ParseBinsFromCue(candidate)
                    .Any(bin => string.Equals(Path.GetFileName(bin), sourceFileName, StringComparison.OrdinalIgnoreCase)));

            if (cuePath is not null)
            {
                paths.Add(cuePath);
                paths.AddRange(ConversionSource.ParseBinsFromCue(cuePath)
                    .Select(bin => Path.Combine(sourceDir, Path.GetFileName(bin))));
            }
        }
        else if (format == RomFormat.Ccd)
        {
            paths.Add(Path.ChangeExtension(sourcePath, ".ccd"));
        }
        else if (format == RomFormat.Gdi)
        {
            string sourceFileName = Path.GetFileName(sourcePath);
            string? gdiPath = Path.GetExtension(sourcePath).Equals(".gdi", StringComparison.OrdinalIgnoreCase)
                ? sourcePath
                : Directory.EnumerateFiles(sourceDir, "*.gdi")
                    .FirstOrDefault(candidate =>
                    {
                        try
                        {
                            return GdiFile.Parse(candidate).Tracks
                                .Any(track => string.Equals(track.FileName, sourceFileName, StringComparison.OrdinalIgnoreCase));
                        }
                        catch
                        {
                            return false;
                        }
                    });

            if (gdiPath is not null)
            {
                var gdi = GdiFile.Parse(gdiPath);
                paths.Add(gdiPath);
                paths.AddRange(gdi.Tracks.Select(gdi.GetTrackFullPath));
            }
        }

        return [.. paths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    public string Create(string sourcePath, IEnumerable<string> sourcePaths, string outputDir, IEnumerable<string> outputEntries, string? packageRoot = null)
    {
        var originalPaths = sourcePaths
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var generatedPaths = outputEntries
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (originalPaths.Count == 0)
            throw new InvalidOperationException("EL 패키지에 보관할 원본 파일을 찾을 수 없습니다.");

        if (generatedPaths.Count == 0)
            throw new InvalidOperationException("EL 패키지로 이동할 패치 결과물을 찾을 수 없습니다.");

        string sourceDir = Path.GetDirectoryName(originalPaths[0])!;

        if (originalPaths.Any(path => !string.Equals(Path.GetDirectoryName(path), sourceDir, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("EL 패키지는 같은 폴더에 있는 원본 파일만 함께 처리할 수 있습니다.");

        var originalPathSet = originalPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var preservedM3uPaths = Directory.EnumerateFiles(sourceDir, "*.m3u", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool hasUnrelatedFiles = Directory.EnumerateFiles(sourceDir)
            .Select(Path.GetFullPath)
            .Any(path => !originalPathSet.Contains(path) && !preservedM3uPaths.Contains(path));
        string sourceName = Path.GetFileNameWithoutExtension(sourcePath);
        string resultDir = string.IsNullOrWhiteSpace(packageRoot)
            ? hasUnrelatedFiles
                ? Utils.GetUniqueFolderPath(Path.Combine(sourceDir, string.IsNullOrWhiteSpace(sourceName) ? "patched" : sourceName))
                : sourceDir
            : Path.GetFullPath(packageRoot);
        string sourceBackupDir = Path.Combine(resultDir, "_EL_CONFIG", "src");

        Directory.CreateDirectory(resultDir);
        Directory.CreateDirectory(sourceBackupDir);

        var sourceMoves = originalPaths
            .Select(path => (From: path, To: Utils.GetUniqueFilePath(Path.Combine(sourceBackupDir, Path.GetFileName(path)))))
            .ToList();
        var outputMoves = generatedPaths
            .Select(path => (From: path, To: Path.Combine(resultDir, Path.GetFileName(path))))
            .Where(move => !string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var m3uMoves = preservedM3uPaths
            .Select(path => (From: path, To: Path.Combine(resultDir, Path.GetFileName(path))))
            .Where(move => !string.Equals(move.From, move.To, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var move in outputMoves)
        {
            if ((File.Exists(move.To) || Directory.Exists(move.To)) && !originalPathSet.Contains(move.To))
                throw new IOException($"EL 패키지 결과 경로가 이미 존재합니다: {move.To}");
        }

        var completedSourceMoves = new List<(string From, string To)>();
        var completedOutputMoves = new List<(string From, string To)>();
        var completedM3uMoves = new List<(string From, string To)>();

        try
        {
            foreach (var move in sourceMoves)
            {
                File.Move(move.From, move.To);
                completedSourceMoves.Add(move);
            }

            foreach (var move in outputMoves)
            {
                MoveEntry(move.From, move.To);
                completedOutputMoves.Add(move);
            }

            foreach (var move in m3uMoves)
            {
                File.Move(move.From, move.To);
                completedM3uMoves.Add(move);
            }

            string patchLogPath = Path.Combine(sourceBackupDir, "patch.log");
            File.AppendAllLines(patchLogPath,
            [
                Path.GetFileName(sourcePath)
            ], Encoding.UTF8);

            if (!string.Equals(sourceDir, resultDir, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(sourceDir)
                && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir);
                log($"EL 빈 원본 폴더 정리: {sourceDir}", LogLevel.Ok);
            }

            log($"EL 패키지 생성 완료: {resultDir}", LogLevel.Ok);
            return resultDir;
        }
        catch
        {
            foreach (var move in completedM3uMoves.AsEnumerable().Reverse())
                File.Move(move.To, move.From);

            foreach (var move in completedOutputMoves.AsEnumerable().Reverse())
                MoveEntry(move.To, move.From);

            foreach (var move in completedSourceMoves.AsEnumerable().Reverse())
                File.Move(move.To, move.From);

            throw;
        }
    }

    public void CleanupOutputDirectory(string outputDir)
    {
        try
        {
            if (!Directory.Exists(outputDir))
                return;

            Directory.Delete(outputDir, true);
            log("EL 패키지 생성 후 output 폴더 정리 완료", LogLevel.Ok);
        }
        catch (Exception ex)
        {
            log($"EL 패키지 생성 후 output 폴더 정리 실패: {ex.Message}", LogLevel.Error);
        }
    }

    public void InstallPackageResult(string packageRoot, IEnumerable<string> sourcePaths, string outputDir, IEnumerable<string> outputEntries, string? obsoleteResultStem = null, IReadOnlyDictionary<string, PackageResultTarget>? targetOverrides = null)
    {
        var generatedPaths = outputEntries
            .Where(path => File.Exists(path) || Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sourceFileNames = sourcePaths
            .Select(path => Path.GetFileName(path)!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var generatedPath in generatedPaths)
        {
            var resultTarget = targetOverrides is not null && targetOverrides.TryGetValue(generatedPath, out var overrideTarget)
                ? overrideTarget
                : ResolveResultTargetPath(packageRoot, generatedPath, sourceFileNames, obsoleteResultStem);
            string targetPath = resultTarget.TargetPath;

            if (File.Exists(generatedPath) && File.Exists(targetPath))
            {
                log($"EL 기존 결과 교체: {Path.GetFileName(targetPath)}", LogLevel.Highlight);
                File.Move(generatedPath, targetPath, true);
            }
            else if (Directory.Exists(targetPath))
                throw new IOException($"EL 패키지 결과 경로가 이미 존재합니다: {targetPath}");
            else
                MoveEntry(generatedPath, targetPath);

            if (resultTarget.ObsoletePath is not null && File.Exists(resultTarget.ObsoletePath))
            {
                log($"EL 기존 형식 결과 삭제: {Path.GetFileName(resultTarget.ObsoletePath)}", LogLevel.Highlight);
                File.Delete(resultTarget.ObsoletePath);
            }
        }

        CleanupOutputDirectory(outputDir);
    }

    public static IReadOnlyDictionary<string, PackageResultTarget>? CreateMultiDiscTargetOverrides(string packageRoot, IReadOnlyList<IReadOnlyList<string>> outputEntriesByDisc)
    {
        var generatedFilesByDisc = outputEntriesByDisc
            .Select(entries => entries.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        if (generatedFilesByDisc.Any(files => files.Count != 1))
            return null;

        var existingDiscFiles = Directory.EnumerateFiles(packageRoot, "*", SearchOption.TopDirectoryOnly)
            .Where(IsDiscResultFile)
            .OrderBy(path => GetDiscOrder(path).Kind)
            .ThenBy(path => GetDiscOrder(path).Value)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (existingDiscFiles.Count != generatedFilesByDisc.Count)
            return null;

        var targets = new Dictionary<string, PackageResultTarget>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < generatedFilesByDisc.Count; index++)
        {
            string generatedPath = generatedFilesByDisc[index][0];
            string existingPath = existingDiscFiles[index];
            string targetPath = Path.ChangeExtension(existingPath, Path.GetExtension(generatedPath));
            string? obsoletePath = string.Equals(existingPath, targetPath, StringComparison.OrdinalIgnoreCase) ? null : existingPath;

            targets[generatedPath] = new PackageResultTarget(targetPath, obsoletePath);
        }

        return targets;
    }

    private static PackageResultTarget ResolveResultTargetPath(string packageRoot, string generatedPath, IReadOnlySet<string> sourceFileNames, string? obsoleteResultStem)
    {
        string generatedFileName = Path.GetFileName(generatedPath);
        string directTargetPath = Path.Combine(packageRoot, generatedFileName);

        if (File.Exists(directTargetPath))
            return new PackageResultTarget(directTargetPath);

        if (File.Exists(generatedPath))
        {
            string outputExtension = Path.GetExtension(generatedPath);

            var sameExtensionTargets = Directory.EnumerateFiles(packageRoot)
                .Where(path => string.Equals(Path.GetExtension(path), outputExtension, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sameExtensionTargets.Count == 1)
                return new PackageResultTarget(sameExtensionTargets[0]);

            if (!string.IsNullOrWhiteSpace(obsoleteResultStem))
            {
                string obsoletePath = Path.Combine(packageRoot, obsoleteResultStem + outputExtension);

                if (File.Exists(obsoletePath))
                    return new PackageResultTarget(directTargetPath, obsoletePath);
            }

            var similarTargets = Directory.EnumerateFiles(packageRoot)
                .Where(path => HasSimilarGameName(Path.GetFileNameWithoutExtension(path), Path.GetFileNameWithoutExtension(generatedPath)))
                .Where(path => !string.Equals(Path.GetExtension(path), outputExtension, StringComparison.OrdinalIgnoreCase)
                    || HasSimilarSize(new FileInfo(path).Length, new FileInfo(generatedPath).Length))
                .OrderBy(path => Math.Abs(new FileInfo(path).Length - new FileInfo(generatedPath).Length))
                .ToList();

            if (similarTargets.Count == 1)
            {
                string similarTarget = similarTargets[0];

                return string.Equals(Path.GetExtension(similarTarget), outputExtension, StringComparison.OrdinalIgnoreCase)
                    ? new PackageResultTarget(similarTarget)
                    : new PackageResultTarget(directTargetPath, similarTarget);
            }
        }

        string generatedStem = TrimUniqueSuffix(Path.GetFileNameWithoutExtension(generatedFileName));
        string generatedExtension = Path.GetExtension(generatedFileName);
        string? matchingSourceFileName = sourceFileNames.FirstOrDefault(fileName =>
            string.Equals(Path.GetExtension(fileName), generatedExtension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TrimUniqueSuffix(Path.GetFileNameWithoutExtension(fileName)), generatedStem, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(packageRoot, fileName)));

        return matchingSourceFileName is null
            ? new PackageResultTarget(directTargetPath)
            : new PackageResultTarget(Path.Combine(packageRoot, matchingSourceFileName));
    }

    private static bool IsDiscResultFile(string path) =>
        !Path.GetExtension(path).Equals(".m3u", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(Path.GetFileNameWithoutExtension(path), @"\b(?:disc|disk)\s*(?:\d+|[a-z])\b", RegexOptions.IgnoreCase);

    private static (int Kind, int Value) GetDiscOrder(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"\b(?:disc|disk)\s*(\d+|[a-z])\b", RegexOptions.IgnoreCase);

        if (!match.Success)
            return (2, int.MaxValue);

        string value = match.Groups[1].Value;

        return int.TryParse(value, out int number)
            ? (0, number)
            : (1, char.ToUpperInvariant(value[0]) - 'A');
    }

    private static string TrimUniqueSuffix(string fileStem)
    {
        int suffixStart = fileStem.LastIndexOf(" (", StringComparison.Ordinal);

        return suffixStart >= 0 && fileStem.EndsWith(')') && int.TryParse(fileStem[(suffixStart + 2)..^1], out _)
            ? fileStem[..suffixStart]
            : fileStem;
    }

    private static bool HasSimilarGameName(string first, string second)
    {
        string normalizedFirst = NormalizeGameName(first);
        string normalizedSecond = NormalizeGameName(second);

        if (string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase))
            return true;

        string shorter = normalizedFirst.Length <= normalizedSecond.Length ? normalizedFirst : normalizedSecond;
        string longer = normalizedFirst.Length > normalizedSecond.Length ? normalizedFirst : normalizedSecond;

        return shorter.Length >= 12
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase)
            && !char.IsLetterOrDigit(longer[shorter.Length])
            && shorter.Length * 100 >= longer.Length * 80;
    }

    private static string NormalizeGameName(string fileStem) =>
        Regex.Replace(
            Regex.Replace(fileStem, @"\([^)]*\)|\[[^]]*\]", string.Empty),
            @"[^\p{L}\p{N}]+",
            " ").Trim();

    private static bool HasSimilarSize(long first, long second) =>
        Math.Abs(first - second) <= Math.Max(1024 * 1024, Math.Max(first, second) / 5);

    private static void MoveEntry(string from, string to)
    {
        if (File.Exists(from))
            File.Move(from, to);
        else if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            throw new FileNotFoundException("이동할 파일 또는 디렉터리를 찾을 수 없습니다.", from);
    }
}
