using CHD.Core.Services;
using Common;
using Patch.Core.Formats.DCP.Services;
using RomForge.Core.Models.Compression;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace RomForge.Core.Services.Patch;

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

    public string Create(string sourcePath, IEnumerable<string> sourcePaths, string outputDir, IEnumerable<string> outputEntries)
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
        string resultDir = hasUnrelatedFiles
            ? Utils.GetUniqueFolderPath(Path.Combine(sourceDir, string.IsNullOrWhiteSpace(sourceName) ? "patched" : sourceName))
            : sourceDir;
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

    public void InstallPackageResult(string packageRoot, IEnumerable<string> sourcePaths, string outputDir, IEnumerable<string> outputEntries, string? obsoleteResultStem = null)
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
            var resultTarget = ResolveResultTargetPath(packageRoot, generatedPath, sourceFileNames, obsoleteResultStem);
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

    private static (string TargetPath, string? ObsoletePath) ResolveResultTargetPath(string packageRoot, string generatedPath, IReadOnlySet<string> sourceFileNames, string? obsoleteResultStem)
    {
        string generatedFileName = Path.GetFileName(generatedPath);
        string directTargetPath = Path.Combine(packageRoot, generatedFileName);

        if (File.Exists(directTargetPath))
            return (directTargetPath, null);

        if (File.Exists(generatedPath))
        {
            string outputExtension = Path.GetExtension(generatedPath);

            if (!string.IsNullOrWhiteSpace(obsoleteResultStem))
            {
                string obsoletePath = Path.Combine(packageRoot, obsoleteResultStem + outputExtension);

                if (File.Exists(obsoletePath))
                    return (directTargetPath, obsoletePath);
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
                    ? (similarTarget, null)
                    : (directTargetPath, similarTarget);
            }
        }

        string generatedStem = TrimUniqueSuffix(Path.GetFileNameWithoutExtension(generatedFileName));
        string generatedExtension = Path.GetExtension(generatedFileName);
        string? matchingSourceFileName = sourceFileNames.FirstOrDefault(fileName =>
            string.Equals(Path.GetExtension(fileName), generatedExtension, StringComparison.OrdinalIgnoreCase)
            && string.Equals(TrimUniqueSuffix(Path.GetFileNameWithoutExtension(fileName)), generatedStem, StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(packageRoot, fileName)));

        return matchingSourceFileName is null
            ? (directTargetPath, null)
            : (Path.Combine(packageRoot, matchingSourceFileName), null);
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
        Regex.Replace(Regex.Replace(fileStem, @"\([^)]*\)|\[[^]]*\]", string.Empty), @"\s+", " ").Trim();

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
