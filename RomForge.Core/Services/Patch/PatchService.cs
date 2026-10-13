using Common;
using Patch.Core;
using Patch.Core.Enums;
using Patch.Core.Services;
using RomForge.Core.Models.Patch;
using System.IO;
using System.IO.Compression;

namespace RomForge.Core.Services.Patch;

public static class PatchService
{
    public static async Task ApplyPatchedArchiveAsync(string sourceArchivePath, string outputZipPath, IReadOnlyDictionary<string, PatchEntry> patchesByEntryName, IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        var outputDir = Path.GetDirectoryName(outputZipPath);

        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        using var sourceArchive = ArchivePatchSourceFactory.Open(sourceArchivePath);
        using var outputStream = new FileStream(outputZipPath, FileMode.Create, FileAccess.Write);
        using var outputZip = new ZipArchive(outputStream, ZipArchiveMode.Create);

        foreach (string entryPath in sourceArchive.EntryPaths)
        {
            ct.ThrowIfCancellationRequested();

            var sourceEntry = sourceArchive.FindEntry(entryPath)
                ?? throw new FileNotFoundException($"원본 압축파일 엔트리를 찾을 수 없습니다: {entryPath}");

            if (patchesByEntryName.TryGetValue(entryPath, out var patchEntry))
                await WritePatchedEntryAsync(sourceEntry, patchEntry, outputZip, progress, ct);
            else
                await CopyEntryAsync(sourceEntry, outputZip, ct);
        }
    }

    private static async Task WritePatchedEntryAsync(IArchivePatchEntry sourceEntry, PatchEntry patchEntry, ZipArchive outputZip, IProgress<ProgressInfo>? progress, CancellationToken ct = default)
    {
        byte[] sourceBytes;

        using (var srcStream = sourceEntry.Open())
        using (var ms = new MemoryStream(checked((int)sourceEntry.Length)))
        {
            await srcStream.CopyToAsync(ms, ct);
            sourceBytes = ms.Length == ms.Capacity ? ms.GetBuffer() : ms.ToArray();
        }

        var patchBytes = await ReadPatchBytesAsync(patchEntry, ct);

        ct.ThrowIfCancellationRequested();

        var entryProgress = progress == null ? null : new Progress<ProgressInfo>(p =>
        {
            progress.Report(new ProgressInfo
            {
                Label = sourceEntry.FullPath,
                Percent = p.Percent
            });
        });

        var resultBytes = UniversalPatcher.DetectFormat(patchBytes) is PatchFormat.Xdelta
            ? await ApplyXdeltaPatchAsync(sourceBytes, patchBytes, entryProgress, ct)
            : await UniversalPatcher.ApplyPatchAsync(sourceBytes, patchBytes, entryProgress, ct);

        var newEntry = outputZip.CreateEntry(sourceEntry.FullPath, CompressionLevel.Optimal);

        using (var destStream = newEntry.Open())
            await destStream.WriteAsync(resultBytes, ct);

        progress?.Report(new ProgressInfo { Label = sourceEntry.FullPath, Percent = 100 });
    }

    private static async Task CopyEntryAsync(IArchivePatchEntry sourceEntry, ZipArchive outputZip, CancellationToken ct)
    {
        var newEntry = outputZip.CreateEntry(sourceEntry.FullPath, CompressionLevel.Optimal);

        using var srcStream = sourceEntry.Open();
        using var destStream = newEntry.Open();

        await srcStream.CopyToAsync(destStream, ct);
    }

    private static async Task<byte[]> ReadPatchBytesAsync(PatchEntry patchEntry, CancellationToken ct)
    {
        if (!patchEntry.IsArchiveEntry)
            return await File.ReadAllBytesAsync(patchEntry.EntryPath, ct);

        using var archive = ArchivePatchSourceFactory.Open(patchEntry.ArchivePath!);
        var entry = archive.FindEntry(patchEntry.EntryPath)
            ?? throw new FileNotFoundException($"패치 압축파일 엔트리를 찾을 수 없습니다: {patchEntry.EntryPath}");

        using var stream = entry.Open();
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);

        return ms.ToArray();
    }

    private static async Task<byte[]> ApplyXdeltaPatchAsync(byte[] sourceBytes, byte[] patchBytes, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "RomForge", "arcade-xdelta-" + Guid.NewGuid().ToString("N"));
        string sourcePath = Path.Combine(workDirectory, "source.bin");
        string patchPath = Path.Combine(workDirectory, "patch.xdelta");
        string outputPath = Path.Combine(workDirectory, "output.bin");
        Directory.CreateDirectory(workDirectory);

        try
        {
            await File.WriteAllBytesAsync(sourcePath, sourceBytes, ct);
            await File.WriteAllBytesAsync(patchPath, patchBytes, ct);
            await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, outputPath, progress, ct);
            return await File.ReadAllBytesAsync(outputPath, ct);
        }
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
    }
}
