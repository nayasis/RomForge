using System.IO;

namespace RomForge.Core.Models.Util;

public sealed class M3uFileItem(string filePath)
{
    public string FilePath { get; } = filePath;

    public string GroupPath => Path.GetDirectoryName(FilePath) ?? string.Empty;

    public string GroupName => Path.GetFileName(GroupPath);

    public string FileName => Path.GetFileName(FilePath);
}
