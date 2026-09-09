using System.IO;
using System.Linq;
using System.Text;

namespace RomForge.Core.Services;

public sealed record M3uCreateResult(string Path, bool Created);

public static class M3uFileCreator
{
    public static M3uCreateResult CreateOrOverwrite(string directoryPath, IEnumerable<string> mediaPaths)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"M3U를 만들 디렉토리를 찾을 수 없습니다: {directoryPath}");

        string? existingPath = Directory.EnumerateFiles(directoryPath, "*.m3u", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();
        var fileNames = ResolveFileNames(mediaPaths, sort: false);
        string directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        string outputPath = existingPath ?? Path.Combine(directoryPath, $"{directoryName}.m3u");

        File.WriteAllLines(outputPath, fileNames, new UTF8Encoding(false));
        return new M3uCreateResult(outputPath, existingPath is null);
    }

    public static M3uCreateResult CreateIfMissing(string directoryPath, IEnumerable<string> mediaPaths)
    {
        if (!Directory.Exists(directoryPath))
            throw new DirectoryNotFoundException($"M3U를 만들 디렉토리를 찾을 수 없습니다: {directoryPath}");

        string? existingPath = Directory.EnumerateFiles(directoryPath, "*.m3u", SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        if (existingPath is not null)
            return new M3uCreateResult(existingPath, false);

        var fileNames = ResolveFileNames(mediaPaths, sort: true);
        string directoryName = Path.GetFileName(Path.TrimEndingDirectorySeparator(directoryPath));
        string outputPath = Path.Combine(directoryPath, $"{directoryName}.m3u");

        File.WriteAllLines(outputPath, fileNames, new UTF8Encoding(false));
        return new M3uCreateResult(outputPath, true);
    }

    private static List<string> ResolveFileNames(IEnumerable<string> mediaPaths, bool sort)
    {
        var fileNames = mediaPaths
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(fileName => !string.IsNullOrWhiteSpace(fileName))
            .Where(fileName => !Path.GetExtension(fileName).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (sort)
            fileNames = fileNames.OrderBy(fileName => fileName, StringComparer.Ordinal);

        var resolvedFileNames = fileNames
            .ToList();

        if (resolvedFileNames.Count == 0)
            throw new InvalidOperationException("M3U에 기록할 파일이 없습니다.");

        return resolvedFileNames;
    }
}
