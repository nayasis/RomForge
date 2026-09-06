using Patch.Core.Formats.DCP.Models;

namespace Patch.Core.Formats.DCP.Services;

public static class GdRomRebuilder
{
    public static void RebuildFull(GdiFile originalGdi, Dictionary<string, byte[]> replacedFiles, byte[]? replacementIpBin, string outputDir, Action<double, string>? onProgress = null, Action<string>? onLog = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(outputDir);

        onProgress?.Invoke(0.0, "기존 GD-ROM 미디어 해석 준비 중...");

        var sourceReader = new GdRomCompositeSectorReader(originalGdi);
        var sourceFunc = sourceReader.AsFunc();
        var firstDataTrack = originalGdi.Tracks
            .Where(t => t.Type == TrackType.Data && t.StartLba >= 45000)
            .OrderBy(t => t.StartLba)
            .First();
        var originalDataTrackPath = originalGdi.GetTrackFullPath(firstDataTrack);
        long originalDataLength = new FileInfo(originalDataTrackPath).Length - firstDataTrack.FileOffset;

        if (originalDataLength <= 0 || originalDataLength % firstDataTrack.SectorSize != 0)
            throw new InvalidDataException($"원본 데이터 트랙 크기가 섹터 크기와 맞지 않습니다: {firstDataTrack.FileName}");

        int originalSectorCount = checked((int)(originalDataLength / firstDataTrack.SectorSize));
        var ipBinSectors = new byte[16][];

        if (replacementIpBin is not null)
        {
            if (replacementIpBin.Length != 16 * 2048)
                throw new InvalidDataException($"DCP bootsector/IP.BIN 크기가 올바르지 않습니다: {replacementIpBin.Length:N0} bytes");

            for (int s = 0; s < 16; s++)
            {
                var sector = new byte[2048];
                Buffer.BlockCopy(replacementIpBin, s * 2048, sector, 0, sector.Length);
                ipBinSectors[s] = sector;
            }
        }
        else
        {
            for (uint s = 0; s < 16; s++)
                ipBinSectors[s] = sourceFunc((uint)firstDataTrack.StartLba + s);
        }

        var originalPvdLba = sourceReader.PvdAbsoluteLba;
        var pvdRaw = sourceFunc(originalPvdLba);
        var root = Iso9660DirectoryReader.ReadTree(sourceFunc, originalPvdLba);
        var builder = new Iso9660Builder(pvdRaw, root);
        var allFiles = Iso9660DirectoryReader.Flatten(root).ToList();
        var fileDataCache = new Dictionary<Iso9660Entry, byte[]>();
        int fileDone = 0;
        int replacedCount = 0;

        try
        {
            foreach (var entry in allFiles)
            {
                ct.ThrowIfCancellationRequested();

                var lookupKey = entry.FullPath.Replace('/', '\\');
                var isReplaced = replacedFiles.TryGetValue(lookupKey, out var patched);
                var data = isReplaced ? patched! : Iso9660DirectoryReader.ReadFile(sourceFunc, entry);

                if (isReplaced)
                {
                    replacedCount++;
                    onLog?.Invoke($"파일 교체: {entry.FullPath} (원본 {entry.Size:N0} → 교체 {data.Length:N0} bytes)");
                }

                builder.SetFileData(entry, data);

                fileDataCache[entry] = data;

                fileDone++;

                onProgress?.Invoke(0.01 * fileDone / allFiles.Count, $"패치 적용 중 ({fileDone:N0}/{allFiles.Count:N0})");
            }
        }
        finally
        {
            sourceReader.Dispose();
        }

        onLog?.Invoke($"총 {replacedCount}개 파일 교체됨 (DCP 패키지 내 파일 {replacedFiles.Count}개 중)");

        builder.Relayout((uint)firstDataTrack.StartLba + 18);

        string bootFileName = System.Text.Encoding.ASCII.GetString(ipBinSectors[0], 0x60, 16).Trim('\0', ' ');
        var bootEntry = allFiles.SingleOrDefault(entry => entry.Name.Equals(bootFileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException($"IP.BIN이 지정한 부트 파일을 찾을 수 없습니다: {bootFileName}");
        int bootSectorCount = (int)Math.Ceiling(fileDataCache[bootEntry].Length / 2048.0);
        long bootLbaValue = firstDataTrack.StartLba + originalSectorCount - 150L - bootSectorCount;

        if (bootLbaValue < firstDataTrack.StartLba)
            throw new InvalidDataException($"데이터 트랙에 부트 파일을 배치할 공간이 없습니다: {bootFileName}");

        uint bootLba = checked((uint)bootLbaValue);
        uint contentEndLba = allFiles
            .Where(entry => entry != bootEntry)
            .Max(entry => entry.LayoutLba + (uint)Math.Ceiling(fileDataCache[entry].Length / 2048.0));

        if (contentEndLba > bootLba)
            throw new InvalidDataException($"패치 결과가 데이터 트랙 용량을 초과했습니다: {bootFileName} 배치 불가");

        bootEntry.LayoutLba = bootLba;

        var contentSectors = new List<(uint Lba, byte[] Data)>();

        foreach (var (lba, data) in builder.GetPathTableSectors())
            contentSectors.Add((lba, data));

        int dirDone = 0;
        int dirTotal = CountDirs(root);

        void CollectDirRecords(Iso9660Entry dir)
        {
            contentSectors.Add((dir.LayoutLba, Iso9660Builder.BuildDirectoryRecordData(dir)));

            foreach (var child in dir.Children.Where(c => !c.IsDirectory))
            {
                var data = fileDataCache[child];

                contentSectors.Add((child.LayoutLba, PadToSector(data)));
            }

            dirDone++;
            onProgress?.Invoke(0.01 + 0.01 * dirDone / dirTotal, $"ISO9660 디렉토리 테이블 및 구조 재배치 중 ({dirDone}/{dirTotal})");

            foreach (var child in dir.Children.Where(c => c.IsDirectory))
                CollectDirRecords(child);
        }

        CollectDirRecords(root);

        var newPvd = builder.BuildPvd((uint)originalSectorCount);

        contentSectors.Add(((uint)firstDataTrack.StartLba + 16, PadToSector(newPvd)));
        contentSectors.Add(((uint)firstDataTrack.StartLba + 17, BuildVolumeDescriptorTerminator()));

        var expanded = new List<(uint Lba, byte[] Sector2048)>();

        for (uint s = 0; s < 16; s++)
            expanded.Add(((uint)firstDataTrack.StartLba + s, ipBinSectors[s]));

        foreach (var (lba, data) in contentSectors)
        {
            int sectorCount = (int)Math.Ceiling(data.Length / 2048.0);

            for (int s = 0; s < sectorCount; s++)
            {
                var chunk = new byte[2048];
                int copyLen = Math.Min(2048, data.Length - s * 2048);

                Buffer.BlockCopy(data, s * 2048, chunk, 0, copyLen);
                expanded.Add(((uint)(lba + s), chunk));
            }
        }

        onProgress?.Invoke(0.02, "중복 섹터 병합 및 정렬 프로세스 시작...");

        var dedup = expanded.GroupBy(e => e.Lba).Select(g => g.Last()).OrderBy(e => e.Lba).ToList();
        var newDataTrackPath = Path.Combine(outputDir, firstDataTrack.FileName);

        GdRomWriter.WriteDataTrack(newDataTrackPath, (uint)firstDataTrack.StartLba, dedup, originalSectorCount, (p, msg) =>
        {
            onProgress?.Invoke(0.02 + (p * 0.23), $"{firstDataTrack.FileName} 생성 중: {msg}");
        }, ct);

        onProgress?.Invoke(0.25, "나머지 트랙 분석 중...");

        var otherTracks = originalGdi.Tracks.Where(t => t != firstDataTrack).ToList();
        long totalBytesToCopy = 0;
        var trackFileInfoList = new List<(string Src, string Dst, string Name, long Length)>();

        foreach (var track in otherTracks)
        {
            var src = originalGdi.GetTrackFullPath(track);
            var dst = Path.Combine(outputDir, track.FileName);

            if (File.Exists(src))
            {
                long len = new FileInfo(src).Length;

                totalBytesToCopy += len;

                trackFileInfoList.Add((src, dst, track.FileName, len));
            }
        }

        long totalBytesCopied = 0;
        byte[] buffer = new byte[64 * 1024];

        foreach (var (src, dst, name, length) in trackFileInfoList)
        {
            if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
            {
                totalBytesCopied += length;
                continue;
            }

            using var sourceStream = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024);
            using var destStream = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
            int bytesRead;

            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();

                destStream.Write(buffer, 0, bytesRead);

                totalBytesCopied += bytesRead;

                if (totalBytesToCopy > 0)
                {
                    double currentPercentage = 0.25 + 0.73 * ((double)totalBytesCopied / totalBytesToCopy);
                    double copiedMb = totalBytesCopied / 1024.0 / 1024.0;
                    double totalMb = totalBytesToCopy / 1024.0 / 1024.0;

                    onProgress?.Invoke(currentPercentage, $"자원 트랙 복사 중: {name} ({copiedMb:N1}MB / {totalMb:N1}MB)");
                }
            }
        }

        onProgress?.Invoke(0.99, "GDI 인덱스 메타파일 최종 검증 및 갱신 중...");
        WriteGdi(originalGdi, Path.Combine(outputDir, Path.GetFileName(originalGdi.GdiPath)));
        onProgress?.Invoke(1.0, "모든 GD-ROM 리빌드 작업 완료!");
    }

    private static int CountDirs(Iso9660Entry dir)
    {
        int count = 1;

        foreach (var child in dir.Children.Where(c => c.IsDirectory))
            count += CountDirs(child);

        return count;
    }

    private static byte[] PadToSector(byte[] data)
    {
        int sectors = (int)Math.Ceiling(data.Length / 2048.0);

        if (data.Length == sectors * 2048)
            return data;

        var padded = new byte[sectors * 2048];

        Buffer.BlockCopy(data, 0, padded, 0, data.Length);

        return padded;
    }

    private static byte[] BuildVolumeDescriptorTerminator()
    {
        var sector = new byte[2048];

        sector[0] = 0xFF;
        System.Text.Encoding.ASCII.GetBytes("CD001").CopyTo(sector, 1);
        sector[6] = 0x01;

        return sector;
    }

    private static void WriteGdi(GdiFile original, string outputPath)
    {
        using var writer = new StreamWriter(outputPath);

        writer.WriteLine(original.Tracks.Count);

        foreach (var t in original.Tracks)
            writer.WriteLine($"{t.Number} {t.StartLba} {(int)t.Type} {t.SectorSize} \"{t.FileName}\" {t.FileOffset}");
    }
}
