using Common;
using Patch.Core;
using Patch.Core.Enums;
using System.IO;
using System.Text;

namespace RomForge.Core.Services.Patch;

public sealed record DirectoryPatchSource(string SourcePath, string? ExistingPackageRoot)
{
    public bool IsExistingElPackage => ExistingPackageRoot is not null;
}

public sealed record DirectoryPatchLocation(string SourceDirectory, string? ExistingPackageRoot)
{
    public bool IsExistingElPackage => ExistingPackageRoot is not null;
}

public class DirectoryPatchSourceResolver(Action<string, LogLevel> log)
{
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
                    return new DirectoryPatchSource(loggedSourcePath, location.ExistingPackageRoot);
            }
        }

        string? discoveredSourcePath = Path.GetExtension(patchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase)
            ? FindDcpSource(candidateDirectory)
            : await FindCompatibleSourceAsync(candidateDirectory, patchPath, ct);

        if (discoveredSourcePath is null)
            return null;

        if (isExistingElPackage)
            UpdatePatchLog(patchLogPath, discoveredSourcePath);

        return new DirectoryPatchSource(discoveredSourcePath, location.ExistingPackageRoot);
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

            string temporaryOutputPath = Path.GetTempFileName();

            try
            {
                File.Delete(temporaryOutputPath);
                await UniversalPatcher.ApplyPatchAsync(candidatePath, patchPath, temporaryOutputPath, null, ct);
                log($"패치 원본 자동 선택: {Path.GetFileName(candidatePath)}", LogLevel.Ok);
                return candidatePath;
            }
            catch (OperationCanceledException)
            {
                throw;
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

        return null;
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
