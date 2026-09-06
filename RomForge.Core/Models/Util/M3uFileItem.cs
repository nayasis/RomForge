using System.IO;

namespace RomForge.Core.Models.Util;

public sealed class M3uFileItem(string filePath)
{
    public string FilePath { get; } = filePath;

    public string GroupName => Path.GetFileName(Path.GetDirectoryName(FilePath)) ?? string.Empty;

    public string FileName => Path.GetFileName(FilePath);
}
