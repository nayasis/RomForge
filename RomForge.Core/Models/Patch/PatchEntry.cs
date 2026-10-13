using System.IO;

namespace RomForge.Core.Models.Patch;

public class PatchEntry
{
    public string DisplayName { get; init; } = string.Empty;
    public string? ArchivePath { get; init; }
    public string EntryPath { get; init; } = string.Empty;
    public bool IsArchiveEntry => ArchivePath is not null;

    public string FileNameWithoutExtension => Path.GetFileNameWithoutExtension(DisplayName);
}
