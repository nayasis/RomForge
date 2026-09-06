using System.IO.Compression;

namespace Patch.Core.Formats.DCP.Services;

public static class DcpGdRomApplier
{
    public static async Task ApplyAsync(string gdiPath, string dcpPath, string outputDir, Action<double, string>? onProgress = null, Action<string>? onLog = null, CancellationToken ct = default)
    {
        onProgress?.Invoke(0.0, "GDI 메타데이터 및 원본 구조 파싱 중...");

        var gdi = GdiFile.Parse(gdiPath);

        Dictionary<string, byte[]> replacedFiles;
        byte[]? replacementIpBin = null;

        {
            using var sourceReader = new GdRomCompositeSectorReader(gdi);
            var sourceFunc = sourceReader.AsFunc();
            var pvdLba = sourceReader.PvdAbsoluteLba;
            var root = Iso9660DirectoryReader.ReadTree(sourceFunc, pvdLba);
            var byPath = Iso9660DirectoryReader.Flatten(root)
                .ToDictionary(e => e.FullPath.Replace('/', '\\'), e => e, StringComparer.OrdinalIgnoreCase);

            replacedFiles = [];
            using var archive = ZipFile.OpenRead(dcpPath);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            int entryDone = 0;

            foreach (var entry in entries)
            {
                var relativePath = entry.FullName.Replace('/', '\\');

                onProgress?.Invoke(0.05 * entryDone / entries.Count, $"DCP 패키지 압축 해제 중: {entry.Name}");

                using var entryStream = entry.Open();
                using var ms = new MemoryStream();

                await entryStream.CopyToAsync(ms, ct);

                if (relativePath.Equals("bootsector\\IP.BIN", StringComparison.OrdinalIgnoreCase))
                {
                    replacementIpBin = ms.ToArray();
                    entryDone++;
                    continue;
                }

                replacedFiles[relativePath] = ms.ToArray();

                entryDone++;
            }

            var xdeltaEntries = replacedFiles.Where(x => x.Key.EndsWith(".xdelta", StringComparison.OrdinalIgnoreCase)).ToList();
            int xdeltaDone = 0;

            onLog?.Invoke($"xdelta 대상 파일 {xdeltaEntries.Count}개 감지됨");

            foreach (var kv in xdeltaEntries)
            {
                var targetPath = kv.Key[..^".xdelta".Length];
                var fileName = Path.GetFileName(targetPath);

                if (!byPath.TryGetValue(targetPath, out var originalEntry))
                {
                    onLog?.Invoke($"[실패] DCP가 참조하는 원본 파일을 찾을 수 없음: {targetPath}");
                    throw new InvalidOperationException($"DCP가 참조하는 원본 파일을 찾을 수 없습니다: {targetPath}");
                }

                onProgress?.Invoke(0.05 + 0.15 * xdeltaDone / xdeltaEntries.Count, $"xdelta 델타 패치 적용 중: {fileName}");

                var originalData = Iso9660DirectoryReader.ReadFile(sourceFunc, originalEntry);
                var patched = await Task.Run(() => DcpXdelta3Codec.Decode(originalData, kv.Value), ct);

                onLog?.Invoke($"xdelta 적용 완료: {targetPath} ({originalData.Length:N0} → {patched.Length:N0} bytes)");

                replacedFiles[targetPath] = patched;
                replacedFiles.Remove(kv.Key);

                xdeltaDone++;
            }
        }

        await Task.Run(() => GdRomRebuilder.RebuildFull(gdi, replacedFiles, replacementIpBin, outputDir,
            (p, msg) => onProgress?.Invoke(0.20 + 0.80 * p, msg), onLog, ct),
            ct);

        onProgress?.Invoke(1.0, "모든 패치 적용 작업 완료!");
    }
}
