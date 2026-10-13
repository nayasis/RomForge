using Patch.Core.Services;
using System.IO;

namespace RomForge.Core.Services.Patch;

public sealed record PatchArchiveExtraction(string WorkDirectory, string? SinglePatchPath)
{
    public bool IsMultiPatch => SinglePatchPath is null;
}

public static class PatchArchiveExtractor
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xdelta", ".xdelta3", ".bps", ".ppf", ".dcp", ".ups", ".ips", ".ips32", ".aps"
    };

    public static bool IsArchivePath(string? path) => ArchivePatchSourceFactory.IsArchivePath(path);

    public static async Task<PatchArchiveExtraction> ExtractAsync(string archivePath, CancellationToken ct)
    {
        using var archive = ArchivePatchSourceFactory.Open(archivePath);
        var candidates = archive.EntryPaths
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .Select(path => archive.FindEntry(path) ?? throw new InvalidOperationException($"압축파일 엔트리를 찾을 수 없습니다: {path}"))
            .ToList();

        if (candidates.Count == 0)
            throw new InvalidOperationException("압축파일 안에 지원하는 패치 파일이 없습니다. 패치 파일로 선택한 압축파일이 아닙니다.");

        var selectedEntries = candidates
            .GroupBy(entry => Path.ChangeExtension(entry.FullPath.Replace('\\', '/'), null), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(entry => GetPriority(Path.GetExtension(entry.FullPath)))
                .ThenBy(entry => entry.FullPath, StringComparer.OrdinalIgnoreCase)
                .First())
            .ToList();

        string workDirectory = Path.Combine(Path.GetTempPath(), "RomForge", "patch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);

        try
        {
            var extractedPaths = new List<string>();

            foreach (var entry in selectedEntries)
            {
                ct.ThrowIfCancellationRequested();

                string fileName = Path.GetFileName(entry.FullPath.Replace('/', Path.DirectorySeparatorChar));
                string destinationPath = GetUniquePath(workDirectory, fileName);

                await using var source = entry.Open();
                await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(destination, ct);

                extractedPaths.Add(destinationPath);
            }

            return new PatchArchiveExtraction(workDirectory, extractedPaths.Count == 1 ? extractedPaths[0] : null);
        }
        catch
        {
            Directory.Delete(workDirectory, true);
            throw;
        }
    }

    public static void Cleanup(PatchArchiveExtraction extraction)
    {
        if (Directory.Exists(extraction.WorkDirectory))
            Directory.Delete(extraction.WorkDirectory, true);
    }

    private static int GetPriority(string extension) => extension.ToLowerInvariant() switch
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

    private static string GetUniquePath(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);

        if (!File.Exists(path))
            return path;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        int index = 2;

        do
        {
            path = Path.Combine(directory, $"{stem} ({index++}){extension}");
        }
        while (File.Exists(path));

        return path;
    }
}
