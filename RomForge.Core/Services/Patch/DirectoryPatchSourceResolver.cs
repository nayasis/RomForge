using Common;
using Patch.Core;
using Patch.Core.Enums;
using Patch.Core.Formats.DCP.Services;
using System.IO;
using System.Text;

namespace RomForge.Core.Services.Patch;

public sealed record DirectoryPatchSource(string SourcePath, string? ExistingPackageRoot, bool IsGdiTrack)
{
    public bool IsExistingElPackage => ExistingPackageRoot is not null;
}

public sealed record DirectoryPatchLocation(string SourceDirectory, string? ExistingPackageRoot)
{
    public bool IsExistingElPackage => ExistingPackageRoot is not null;
}

public class DirectoryPatchSourceResolver(Action<string, LogLevel> log)
{
    private static readonly TimeSpan SourceProbeDuration = TimeSpan.FromSeconds(2);

    private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".nfo", ".diz", ".url", ".ini", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".md5", ".sfv", ".pdf"
    };

    public async Task<DirectoryPatchSource?> ResolveAsync(string directoryPath, string patchPath, CancellationToken ct)
    {
        var location = ResolveLocation(directoryPath);
        bool isExistingElPackage = location.IsExistingElPackage;
        string candidateDirectory = location.SourceDirectory;
        string patchLogPath = Path.Combine(candidateDirectory, "patch.log");

        if (isExistingElPackage && File.Exists(patchLogPath))
        {
            foreach (var fileName in File.ReadLines(patchLogPath).Reverse().Select(Path.GetFileName).Where(name => !string.IsNullOrWhiteSpace(name)))
            {
                string loggedSourcePath = Path.Combine(candidateDirectory, fileName!);

                if (File.Exists(loggedSourcePath))
                    return new DirectoryPatchSource(loggedSourcePath, location.ExistingPackageRoot, IsGdiTrack(loggedSourcePath));
            }
        }

        string? discoveredSourcePath = null;

        foreach (string searchDirectory in EnumerateSourceDirectories(candidateDirectory))
        {
            discoveredSourcePath = Path.GetExtension(patchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase)
                ? FindDcpSource(searchDirectory)
                : await FindCompatibleSourceAsync(searchDirectory, patchPath, ct);

            if (discoveredSourcePath is not null)
                break;
        }

        if (discoveredSourcePath is null)
            return null;

        if (isExistingElPackage)
            UpdatePatchLog(patchLogPath, discoveredSourcePath);

        return new DirectoryPatchSource(discoveredSourcePath, location.ExistingPackageRoot, IsGdiTrack(discoveredSourcePath));
    }

    public async Task<bool> HasCompatibleSourceAsync(string directoryPath, string patchPath, CancellationToken ct)
    {
        if (File.Exists(directoryPath))
            return (await ProbeCandidateAsync(directoryPath, patchPath, ct)).IsMatch;

        var location = ResolveLocation(directoryPath);

        foreach (string searchDirectory in EnumerateSourceDirectories(location.SourceDirectory))
        {
            bool matched = Path.GetExtension(patchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase)
                ? FindDcpSource(searchDirectory) is not null
                : await FindCompatibleSourceAsync(searchDirectory, patchPath, ct) is not null;

            if (matched)
                return true;
        }

        return false;
    }

    public static DirectoryPatchLocation ResolveLocation(string directoryPath)
    {
        string? existingPackageSourceDirectory = FindExistingPackageSourceDirectory(directoryPath);
        return new DirectoryPatchLocation(
            existingPackageSourceDirectory ?? directoryPath,
            existingPackageSourceDirectory is null ? null : GetPackageRoot(existingPackageSourceDirectory));
    }

    private static string? FindExistingPackageSourceDirectory(string directoryPath)
    {
        string nestedSourceDirectory = Path.Combine(directoryPath, "_EL_CONFIG", "src");
        if (Directory.Exists(nestedSourceDirectory))
            return nestedSourceDirectory;

        var sourceDirectory = new DirectoryInfo(directoryPath);
        return sourceDirectory.Name.Equals("src", StringComparison.OrdinalIgnoreCase)
            && sourceDirectory.Parent?.Name.Equals("_EL_CONFIG", StringComparison.OrdinalIgnoreCase) == true
            ? sourceDirectory.FullName
            : null;
    }

    private static string GetPackageRoot(string sourceDirectory) =>
        Directory.GetParent(sourceDirectory)?.Parent?.FullName
        ?? throw new InvalidOperationException("EL 패키지 루트를 확인할 수 없습니다.");

    private static string? FindDcpSource(string candidateDirectory) =>
        Directory.EnumerateFiles(candidateDirectory, "*.gdi", SearchOption.TopDirectoryOnly).FirstOrDefault();

    public static IEnumerable<string> EnumerateSourceDirectories(string rootDirectory)
    {
        yield return rootDirectory;

        if (Directory.EnumerateFiles(rootDirectory, "*", SearchOption.TopDirectoryOnly).Any())
            yield break;

        var queue = new Queue<(string Path, int Depth)>();

        foreach (string childDirectory in Directory.EnumerateDirectories(rootDirectory, "*", SearchOption.TopDirectoryOnly))
            if (!IsOutputDirectory(childDirectory))
                queue.Enqueue((childDirectory, 1));

        while (queue.Count > 0)
        {
            var (directoryPath, depth) = queue.Dequeue();
            yield return directoryPath;

            if (depth >= 3)
                continue;

            foreach (string childDirectory in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.TopDirectoryOnly))
                if (!IsOutputDirectory(childDirectory))
                    queue.Enqueue((childDirectory, depth + 1));
        }
    }

    private static bool IsOutputDirectory(string path)
    {
        string name = Path.GetFileName(path);
        return name.Equals("output", StringComparison.OrdinalIgnoreCase)
            || name.Length == 13 && name.StartsWith("output_", StringComparison.OrdinalIgnoreCase)
                && name[7..].All(char.IsDigit);
    }

    private static bool IsGdiTrack(string sourcePath)
    {
        string? sourceDirectory = Path.GetDirectoryName(sourcePath);

        if (sourceDirectory is null || Path.GetExtension(sourcePath).Equals(".gdi", StringComparison.OrdinalIgnoreCase))
            return false;

        string sourceFileName = Path.GetFileName(sourcePath);

        foreach (string gdiPath in Directory.EnumerateFiles(sourceDirectory, "*.gdi", SearchOption.TopDirectoryOnly))
        {
            try
            {
                if (GdiFile.Parse(gdiPath).Tracks.Any(track => string.Equals(track.FileName, sourceFileName, StringComparison.OrdinalIgnoreCase)))
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    private async Task<string?> FindCompatibleSourceAsync(string candidateDirectory, string patchPath, CancellationToken ct)
    {
        string fullPatchPath = Path.GetFullPath(patchPath);
        string patchBaseName = Path.GetFileNameWithoutExtension(patchPath);
        var candidates = Directory.EnumerateFiles(candidateDirectory)
            .Where(path => !IgnoredExtensions.Contains(Path.GetExtension(path)))
            .Where(path => !string.Equals(Path.GetFullPath(path), fullPatchPath, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(path => string.Equals(Path.GetFileNameWithoutExtension(path), patchBaseName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(path => new FileInfo(path).Length)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1 && await UniversalPatcher.DetectFormatAsync(patchPath, ct) == PatchFormat.Ppf)
        {
            log($"PPF 원본 자동 선택: {Path.GetFileName(candidates[0])}", LogLevel.Ok);
            return candidates[0];
        }

        foreach (var candidatePath in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var probe = await ProbeCandidateAsync(candidatePath, patchPath, ct);

            if (probe.IsMatch)
            {
                string suffix = probe.UsedArchiveContents ? " (압축 내부 검증)" : probe.CompletedQuickly ? string.Empty : " (빠른 검증)";
                log($"패치 원본 자동 선택: {Path.GetFileName(candidatePath)}{suffix}", LogLevel.Ok);
                return candidatePath;
            }
        }

        if (candidates.Count == 1
            && IsCompressedSource(candidates[0])
            && await PatchSourceValidation.SupportsDirectCompressedSourceAsync(patchPath, ct))
        {
            log($"압축 원본 자동 선택: {Path.GetFileName(candidates[0])} (압축 해제 후 검증)", LogLevel.Ok);
            return candidates[0];
        }

        return null;
    }

    private static bool IsCompressedSource(string path) =>
        Path.GetExtension(path).Equals(".chd", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(path).Equals(".rvz", StringComparison.OrdinalIgnoreCase);

    private async Task<(bool IsMatch, bool CompletedQuickly, bool UsedArchiveContents)> ProbeCandidateAsync(string candidatePath, string patchPath, CancellationToken ct)
    {
        string temporaryOutputPath = Path.GetTempFileName();
        using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(SourceProbeDuration);

        try
        {
            File.Delete(temporaryOutputPath);
            await UniversalPatcher.ApplyPatchAsync(candidatePath, patchPath, temporaryOutputPath, null, probeCts.Token);
            return (true, true, false);
        }
        catch (OperationCanceledException)
        {
            if (ct.IsCancellationRequested)
                throw;

            return (true, false, false);
        }
        catch
        {
            if (SourceArchiveExtractor.IsArchivePath(candidatePath)
                && await IsCompatibleArchiveSourceAsync(candidatePath, patchPath, ct))
            {
                return (true, true, true);
            }

            return (false, false, false);
        }
        finally
        {
            if (File.Exists(temporaryOutputPath))
                File.Delete(temporaryOutputPath);
        }
    }

    private async Task<bool> IsCompatibleArchiveSourceAsync(string archivePath, string patchPath, CancellationToken ct)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "RomForge", "source-probe", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(workDirectory);

            var extraction = await SourceArchiveExtractor.AnalyzeAndExtractAsync(
                archivePath,
                workDirectory,
                new Progress<ProgressInfo>(),
                ct);

            var sourcePaths = new List<string>();

            if (extraction.NeedsSelection)
            {
                foreach (var candidate in extraction.Candidates)
                {
                    ct.ThrowIfCancellationRequested();
                    sourcePaths.Add(await SourceArchiveExtractor.ExtractCandidateAsync(
                        archivePath,
                        workDirectory,
                        candidate.EntryKey,
                        new Progress<ProgressInfo>(),
                        ct));
                }
            }
            else if (extraction.ResolvedPath is not null)
            {
                sourcePaths.Add(extraction.ResolvedPath);
            }

            foreach (string sourcePath in sourcePaths)
            {
                string temporaryOutputPath = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
                using var probeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                probeCts.CancelAfter(SourceProbeDuration);

                try
                {
                    await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, temporaryOutputPath, null, probeCts.Token);
                    return true;
                }
                catch (OperationCanceledException)
                {
                    if (ct.IsCancellationRequested)
                        throw;

                    return true;
                }
                catch
                {
                }
                finally
                {
                    if (File.Exists(temporaryOutputPath))
                        File.Delete(temporaryOutputPath);
                }
            }

            return false;
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, true);
        }
    }

    public static void UpdatePatchLog(string patchLogPath, string sourcePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(patchLogPath)!);

        if (File.Exists(patchLogPath) && File.GetAttributes(patchLogPath).HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(patchLogPath, File.GetAttributes(patchLogPath) & ~FileAttributes.ReadOnly);

        File.WriteAllText(patchLogPath, Path.GetFileName(sourcePath), Encoding.UTF8);

        try
        {
            File.SetAttributes(patchLogPath, File.GetAttributes(patchLogPath) & ~FileAttributes.Hidden);
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
