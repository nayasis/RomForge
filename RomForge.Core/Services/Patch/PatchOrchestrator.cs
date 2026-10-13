using CHD.Core.Services;
using Common;
using Patch.Core;
using Patch.Core.Formats.DCP.Services;
using RomForge.Core.Models.Compression;
using System.IO;

namespace RomForge.Core.Services.Patch;

public class PatchOrchestrator(Action<string, LogLevel> log, IProgress<ProgressInfo> progress, bool autoCompress, int dolphinCompressLevel)
{
    private string? _outputCuePath;
    private string? _outputCcdPath;
    private string? _outputGdiPath;
    private List<string> _copiedTrackPaths = [];
    private readonly BinTrackCopier _binTrackCopier = new(log);
    private readonly CcdCompanionCopier _ccdCompanionCopier = new(log);
    private readonly GdiTrackCopier _gdiTrackCopier = new(log);
    private readonly ZipCompressor _zipCompressor = new(log, progress);
    private readonly CompressKnownConverter _compressKnownConverter = new(log, progress, dolphinCompressLevel);

    public async Task PatchAsync(string sourcePath, string patchPath, DetectResult detected, string outputDir, string outputPath, bool sourceIsTemporary, CancellationToken ct)
    {
        ResetState();

        bool isDcpPatch = Path.GetExtension(patchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase);

        if (isDcpPatch)
        {
            if (detected.Format != RomFormat.Gdi)
                throw new InvalidOperationException("DCP 패치는 드림캐스트 GDI 원본(또는 GDI로 변환되는 CHD)에만 적용할 수 있습니다.");

            string gdiPath = sourcePath;

            if (!Path.GetExtension(gdiPath).Equals(".gdi", StringComparison.OrdinalIgnoreCase))
            {
                var sourceDir = Path.GetDirectoryName(sourcePath)!;

                gdiPath = Directory.GetFiles(sourceDir, "*.gdi").FirstOrDefault()
                    ?? throw new InvalidOperationException("DCP 패치 대상 .gdi 파일을 찾을 수 없습니다.");
            }

            string workDir = Path.GetDirectoryName(gdiPath)!;
            string titleName = Path.GetFileNameWithoutExtension(gdiPath);

            if (sourceIsTemporary)
            {
                await DcpGdRomApplier.ApplyAsync(gdiPath, patchPath, workDir,
                    (p, msg) => progress.Report(new ProgressInfo { Percent = (int)(p * 100), Label = msg }),
                    msg => log(msg, LogLevel.Info), ct);

                progress.Report(new ProgressInfo { Label = "패치 완료", Percent = 100 });
                log($"패치 완료: {gdiPath}", LogLevel.Ok);

                Directory.CreateDirectory(outputDir);

                if (autoCompress)
                {
                    progress.Report(new ProgressInfo { Label = "CHD 변환 중...", Percent = 0 });

                    FileConverter converter = new(AppConfig.Instance.Chdman.Compression);
                    converter.LogMessage += (_, e) => log(e.Message, e.Level);

                    var chdResult = await converter.ConvertFileAsync(gdiPath, null, progress, ct);

                    if (!chdResult.Success)
                        throw new Exception($"CHD 변환 실패: {chdResult.Message}");

                    string finalChdPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, titleName + ".chd"));

                    File.Move(chdResult.OutputFile!, finalChdPath);

                    log($"CHD 변환 완료: {finalChdPath}", LogLevel.Ok);
                }
                else
                {
                    string finalDir = Utils.GetUniqueFolderPath(Path.Combine(outputDir, titleName));

                    Directory.Move(workDir, finalDir);

                    log($"결과물 저장 완료: {finalDir}", LogLevel.Ok);
                }
            }
            else
            {
                string dcpOutputDir = Utils.GetUniqueFolderPath(Path.Combine(outputDir, titleName));

                Directory.CreateDirectory(dcpOutputDir);

                await DcpGdRomApplier.ApplyAsync(gdiPath, patchPath, dcpOutputDir,
                    (p, msg) => progress.Report(new ProgressInfo { Percent = (int)(p * 100), Label = msg }),
                    msg => log(msg, LogLevel.Info), ct);

                _outputGdiPath = Path.Combine(dcpOutputDir, Path.GetFileName(gdiPath));

                if (File.Exists(_outputGdiPath))
                {
                    var rebuiltGdi = GdiFile.Parse(_outputGdiPath);

                    foreach (var track in rebuiltGdi.Tracks)
                    {
                        var trackPath = rebuiltGdi.GetTrackFullPath(track);

                        if (File.Exists(trackPath))
                            _copiedTrackPaths.Add(trackPath);
                    }

                    LogDcpChdInputLayout(rebuiltGdi);
                }

                progress.Report(new ProgressInfo { Label = "패치 완료", Percent = 100 });
                log($"패치 완료: {_outputGdiPath}", LogLevel.Ok);

                if (autoCompress && File.Exists(_outputGdiPath))
                {
                    string dcpWorkingDirectory = Path.GetDirectoryName(_outputGdiPath)!;

                    await DcpChdStagingConverter.ConvertAsync(_outputGdiPath, outputDir, AppConfig.Instance.Chdman.Compression, progress, log, ct);

                    if (File.Exists(outputPath))
                        File.Delete(outputPath);

                    File.Delete(_outputGdiPath);

                    foreach (var trackPath in _copiedTrackPaths)
                        if (File.Exists(trackPath))
                            File.Delete(trackPath);

                    _copiedTrackPaths.Clear();

                    if (Directory.Exists(dcpWorkingDirectory) && !Directory.EnumerateFileSystemEntries(dcpWorkingDirectory).Any())
                        Directory.Delete(dcpWorkingDirectory);
                }
            }

            return;
        }

        await UniversalPatcher.ApplyPatchAsync(sourcePath, patchPath, outputPath, progress, ct);

        await FinalizePatchedFileAsync(sourcePath, detected, outputDir, outputPath, sourceIsTemporary, ct);
    }

    public async Task CompletePatchedFileAsync(string sourcePath, DetectResult detected, string outputDir, string outputPath, bool sourceIsTemporary, CancellationToken ct)
    {
        ResetState();
        await FinalizePatchedFileAsync(sourcePath, detected, outputDir, outputPath, sourceIsTemporary, ct);
    }

    private async Task FinalizePatchedFileAsync(string sourcePath, DetectResult detected, string outputDir, string outputPath, bool sourceIsTemporary, CancellationToken ct)
    {
        if (detected.Format is RomFormat.Chd or RomFormat.Rvz)
        {
            progress.Report(new ProgressInfo { Label = "패치 완료", Percent = 100 });
            log($"압축 원본 직접 패치 완료: {outputPath}", LogLevel.Ok);
            return;
        }

        bool isZipTarget = detected.Format is not (RomFormat.Bin or RomFormat.Iso or RomFormat.Gcm or RomFormat.Wii or RomFormat.Wbfs or RomFormat.Ccd or RomFormat.Cci or RomFormat.Cia or RomFormat.Gdi);

        progress.Report(new ProgressInfo { Label = "패치 완료", Percent = 100 });
        log($"패치 완료: {outputPath}", LogLevel.Ok);

        bool skipCompress = false;

        if (detected.Format == RomFormat.Bin)
        {
            if (!autoCompress && !_binTrackCopier.HasMatchingCue(sourcePath))
                return;

            _outputCuePath = await _binTrackCopier.CopyBinTracksAsync(sourcePath, outputDir, outputPath, _copiedTrackPaths, sourceIsTemporary);
            skipCompress = _outputCuePath is null;
        }
        else if (detected.Format == RomFormat.Ccd)
        {
            _outputCcdPath = _ccdCompanionCopier.CopyCcd(sourcePath, outputPath, sourceIsTemporary, _copiedTrackPaths);
            skipCompress = _outputCcdPath is null;
        }
        else if (detected.Format == RomFormat.Gdi)
        {
            _outputGdiPath = _gdiTrackCopier.CopyGdiTracks(sourcePath, outputDir, outputPath, _copiedTrackPaths, sourceIsTemporary);
            skipCompress = _outputGdiPath is null;

            if (_outputGdiPath is not null)
                log($"GDI 재구성 완료: {_outputGdiPath}", LogLevel.Info);
        }

        if (!autoCompress || skipCompress)
            return;

        if (isZipTarget)
        {
            progress.Report(new ProgressInfo { Label = "압축 중...", Percent = 0 });
            await _zipCompressor.CompressFromFileAsync(sourcePath, outputPath, outputDir, ct);
        }
        else
            await _compressKnownConverter.ConvertAsync(detected, outputPath, _outputCuePath, _copiedTrackPaths, _outputCcdPath, _outputGdiPath, outputDir, ct);
    }

    private void ResetState()
    {
        _outputCuePath = null;
        _outputCcdPath = null;
        _outputGdiPath = null;
        _copiedTrackPaths = [];
    }

    private void LogDcpChdInputLayout(GdiFile gdi)
    {
        log($"DCP CHD 입력 GDI: {_outputGdiPath}", LogLevel.Highlight);

        foreach (var track in gdi.Tracks)
        {
            var trackPath = gdi.GetTrackFullPath(track);

            if (File.Exists(trackPath))
                log($"DCP CHD 입력 트랙: {track.FileName} -> {trackPath} ({new FileInfo(trackPath).Length:N0} bytes)", LogLevel.Info);
            else
                log($"DCP CHD 입력 트랙 누락: {track.FileName} -> {trackPath}", LogLevel.Error);
        }
    }

    public void Cleanup(string outputPath)
    {
        try
        {
            if (File.Exists(outputPath))
                File.Delete(outputPath);

            if (_outputCuePath is not null && File.Exists(_outputCuePath))
                File.Delete(_outputCuePath);

            if (_outputCcdPath is not null && File.Exists(_outputCcdPath))
                File.Delete(_outputCcdPath);

            if (_outputGdiPath is not null && File.Exists(_outputGdiPath))
                File.Delete(_outputGdiPath);

            foreach (var trackPath in _copiedTrackPaths)
                if (File.Exists(trackPath))
                    File.Delete(trackPath);
        }
        catch (Exception ex)
        {
            log($"파일 정리 실패: {ex.Message}", LogLevel.Error);
        }
    }
}
