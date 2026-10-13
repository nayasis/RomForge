using Common;
using Common.WPF.ViewModels;
using NSW.WPF.Services;
using RomForge.Core;
using RomForge.Core.Models;
using RomForge.Core.Models.Compression;
using RomForge.Core.Services;
using RomForge.Core.Models.Patch;
using RomForge.Core.Services.Patch;
using RomForge.Core.Services.Compression;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace RomForge.ViewModels.Patch;

public class NormalPatchMainViewModel : ToolTabViewModel, IPatchViewModel
{
    private CancellationTokenSource? _runCts;
    private string? _sourcePath;
    private string? _patchPath;
    private int _progressPct;
    private string _progressLabel = string.Empty;
    private string _progressPercent = "0%";
    private string _progressTime = string.Empty;
    private string _progressSpeed = string.Empty;

    public System.Collections.ObjectModel.ObservableCollection<LogEntry> LogEntries { get; } = [];

    public event EventHandler? PatchCompleted;

    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            _sourcePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SourceLabel));
            OnPropertyChanged(nameof(SourceIconPath));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public string? PatchPath
    {
        get => _patchPath;
        set
        {
            _patchPath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PatchLabel));
            OnPropertyChanged(nameof(PatchIconPath));
        }
    }

    public bool AutoCompress
    {
        get => AppConfig.Instance.Patch.AutoCompress;
        set
        {
            AppConfig.Instance.Patch.AutoCompress = value;
            OnPropertyChanged(nameof(AutoCompress));
        }
    }

    public bool CreateElPackageWhenCompleted
    {
        get => AppConfig.Instance.Patch.CreateElPackageWhenCompleted;
        set
        {
            AppConfig.Instance.Patch.CreateElPackageWhenCompleted = value;
            OnPropertyChanged(nameof(CreateElPackageWhenCompleted));
        }
    }

    public bool AutoScrollLogs
    {
        get => AppConfig.Instance.Patch.AutoScrollLogs;
        set
        {
            AppConfig.Instance.Patch.AutoScrollLogs = value;
            OnPropertyChanged(nameof(AutoScrollLogs));
        }
    }

    public Func<IReadOnlyList<ArchiveCandidate>, Task<string?>>? RequestSourceSelectionAsync { get; set; }

    public string SourceLabel => Path.GetFileName(SourcePath) ?? "원본 파일 또는 디렉토리를 드래그하거나 클릭하세요";

    public string PatchLabel => Path.GetFileName(PatchPath) ?? "패치 파일 또는 디렉토리를 드래그하거나 클릭하세요";

    public string SourceIconPath => Directory.Exists(SourcePath)
        ? "/Common.WPF;component/Assets/Images/AddFolder.png"
        : "/Common.WPF;component/Assets/Images/AddFile.png";

    public string PatchIconPath => Directory.Exists(PatchPath)
        ? "/Common.WPF;component/Assets/Images/AddFolder.png"
        : "/Common.WPF;component/Assets/Images/AddFile.png";

    public int ProgressPct
    {
        get => _progressPct;
        set { _progressPct = value; OnPropertyChanged(); }
    }

    public string ProgressLabel
    {
        get => _progressLabel;
        set { _progressLabel = value; OnPropertyChanged(); }
    }

    public string ProgressPercent
    {
        get => _progressPercent;
        set { _progressPercent = value; OnPropertyChanged(); }
    }

    public string ProgressTime
    {
        get => _progressTime;
        set { _progressTime = value; OnPropertyChanged(); }
    }

    public string ProgressSpeed
    {
        get => _progressSpeed;
        set { _progressSpeed = value; OnPropertyChanged(); }
    }

    public NormalPatchMainViewModel()
    {
        AppConfig.Instance.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(AppConfig.Patch))
            {
                OnPropertyChanged(nameof(AutoCompress));
                OnPropertyChanged(nameof(CreateElPackageWhenCompleted));
                OnPropertyChanged(nameof(AutoScrollLogs));
            }
        };

        AppConfig.Instance.Patch.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatchConfig.AutoCompress))
                OnPropertyChanged(nameof(AutoCompress));

            if (e.PropertyName == nameof(PatchConfig.CreateElPackageWhenCompleted))
                OnPropertyChanged(nameof(CreateElPackageWhenCompleted));

            if (e.PropertyName == nameof(PatchConfig.AutoScrollLogs))
                OnPropertyChanged(nameof(AutoScrollLogs));
        };
    }

    public void Log(string message, LogLevel level)
    {
        Application.Current?.Dispatcher?.Invoke(() => LogEntries.Add(new LogEntry { Message = message, Level = level }));
    }

    public string? OutputDirectory { get; private set; }

    public async Task RunAsync()
    {
        if (SourcePath is null || PatchPath is null)
            return;

        _runCts = new CancellationTokenSource();
        string outputFolderName = "output_" + DateTime.Now.ToString("HHmmss");

        var ct = _runCts.Token;
        bool sourceIsDirectory = Directory.Exists(SourcePath);
        bool patchIsDirectory = Directory.Exists(PatchPath);
        string sourceRootDirectory = sourceIsDirectory ? SourcePath : Path.GetDirectoryName(SourcePath)!;
        string outputDir = Path.Combine(sourceRootDirectory, outputFolderName);
        string? extractDir = null;
        string? outputPath = null;
        string completionDir = outputDir;
        string selectedSourcePath = SourcePath;
        string actualPatchPath = PatchPath;
        bool selectedSourceIsGdiTrack = false;
        string? existingElPackageRoot = !sourceIsDirectory && AppConfig.Instance.Patch.CreateElPackageWhenCompleted
            ? GetElPackageRoot(selectedSourcePath)
            : null;
        string? selectedElPackageRoot = null;
        IReadOnlyList<string>? existingPackageSourcePaths = null;
        bool preserveFailedDcpOutput = false;
        PatchArchiveExtraction? patchArchiveExtraction = null;

        var orchestrator = new PatchOrchestrator(Log, BuildProgressReporter(), AutoCompress, AppConfig.Instance.Dolphin.CompressLevel);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (PatchArchiveExtractor.IsArchivePath(PatchPath))
            {
                Log($"패치 압축파일 확인 중: {Path.GetFileName(PatchPath)}", LogLevel.Highlight);

                patchArchiveExtraction = await PatchArchiveExtractor.ExtractAsync(PatchPath, ct);

                if (patchArchiveExtraction.IsMultiPatch)
                {
                    if (!sourceIsDirectory)
                    {
                        actualPatchPath = await ResolvePatchForSingleSourceAsync(SourcePath, patchArchiveExtraction.WorkDirectory, ct)
                            ?? throw new InvalidOperationException("원본 파일과 일치하는 패치 파일을 압축파일 안에서 찾을 수 없습니다.");
                        Log("원본 파일이 1개여서 단일 패치로 처리합니다.", LogLevel.Highlight);
                        Log($"압축파일 패치 선택: {Path.GetFileName(actualPatchPath)}", LogLevel.Ok);
                    }
                    else
                    {
                        if (await TryRunSingleDiscPatchSetAsync(SourcePath, patchArchiveExtraction.WorkDirectory, outputFolderName, ct))
                            return;

                        var patchMode = await ResolvePatchDirectoryModeAsync(SourcePath, patchArchiveExtraction.WorkDirectory, ct);

                        if (patchMode.IsMultiDisc)
                        {
                            Log("압축파일 안의 여러 패치를 멀티 디스크 패치로 처리합니다.", LogLevel.Highlight);
                            await RunMultiDiscAsync(SourcePath, patchArchiveExtraction.WorkDirectory, outputFolderName, ct);
                            return;
                        }

                        actualPatchPath = patchMode.SinglePatchPath
                            ?? throw new InvalidOperationException("단일 원본에 적용할 패치 파일을 압축파일 안에서 찾을 수 없습니다.");
                        Log("원본 또는 패치가 1개여서 단일 패치로 처리합니다.", LogLevel.Highlight);
                        Log($"압축파일 패치 선택: {Path.GetFileName(actualPatchPath)}", LogLevel.Ok);
                    }
                }
                else
                {
                    actualPatchPath = patchArchiveExtraction.SinglePatchPath!;
                    Log($"압축파일 패치 선택: {Path.GetFileName(actualPatchPath)}", LogLevel.Ok);
                }
            }

            preserveFailedDcpOutput = Path.GetExtension(actualPatchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase);

            if (patchIsDirectory)
            {
                if (!sourceIsDirectory)
                {
                    actualPatchPath = await ResolvePatchForSingleSourceAsync(SourcePath, actualPatchPath, ct)
                        ?? throw new InvalidOperationException("원본 파일과 일치하는 패치 파일을 패치 폴더에서 찾을 수 없습니다.");
                    Log("원본 파일이 1개여서 단일 패치로 처리합니다.", LogLevel.Highlight);
                    Log($"패치 폴더 선택: {Path.GetFileName(actualPatchPath)}", LogLevel.Ok);
                }
                else
                {
                    if (await TryRunSingleDiscPatchSetAsync(SourcePath, actualPatchPath, outputFolderName, ct))
                        return;

                    var patchMode = await ResolvePatchDirectoryModeAsync(SourcePath, actualPatchPath, ct);

                    if (patchMode.IsMultiDisc)
                    {
                        await RunMultiDiscAsync(SourcePath, actualPatchPath, outputFolderName, ct);
                        return;
                    }

                    actualPatchPath = patchMode.SinglePatchPath
                        ?? throw new InvalidOperationException("단일 원본에 적용할 패치 파일을 패치 폴더에서 찾을 수 없습니다.");
                    Log("원본 또는 패치가 1개여서 단일 패치로 처리합니다.", LogLevel.Highlight);
                }
            }

            if (sourceIsDirectory)
            {
                var directorySource = await new DirectoryPatchSourceResolver(Log).ResolveAsync(SourcePath, actualPatchPath, ct);

                if (directorySource is null)
                {
                    ShowSourceSearchWarning();
                    return;
                }

                selectedSourcePath = directorySource.SourcePath;
                selectedSourceIsGdiTrack = directorySource.IsGdiTrack;
                existingElPackageRoot = directorySource.IsExistingElPackage && AppConfig.Instance.Patch.CreateElPackageWhenCompleted
                    ? directorySource.ExistingPackageRoot
                    : null;

                if (CreateElPackageWhenCompleted
                    && !directorySource.IsExistingElPackage
                    && !Directory.EnumerateFiles(SourcePath, "*", SearchOption.TopDirectoryOnly).Any())
                {
                    selectedElPackageRoot = SourcePath;
                    completionDir = selectedElPackageRoot;
                }
            }

            if (existingElPackageRoot is not null)
            {
                string sourceDirectory = Path.Combine(existingElPackageRoot, "_EL_CONFIG", "src");
                existingPackageSourcePaths = [.. Directory.EnumerateFiles(sourceDirectory)
                    .Where(path => !string.Equals(Path.GetFileName(path), "patch.log", StringComparison.OrdinalIgnoreCase))];
                outputDir = Path.Combine(existingElPackageRoot, outputFolderName);
                completionDir = outputDir;
            }

            Directory.CreateDirectory(outputDir);
            OutputDirectory = outputDir;

            string actualSourcePath = selectedSourcePath;

            if (SourceArchiveExtractor.IsArchivePath(selectedSourcePath))
            {
                Log($"원본 압축 확인 중: {Path.GetFileName(selectedSourcePath)}", LogLevel.Highlight);

                extractDir = Path.Combine(outputDir, "_src_" + Path.GetFileNameWithoutExtension(selectedSourcePath));
                Directory.CreateDirectory(extractDir);

                var extractResult = await SourceArchiveExtractor.AnalyzeAndExtractAsync(selectedSourcePath, extractDir, BuildProgressReporter(), ct);

                if (extractResult.NeedsSelection)
                {
                    Log($"압축 안에 패치 대상 후보가 {extractResult.Candidates.Count}개 있습니다. 선택이 필요합니다.", LogLevel.Highlight);

                    if (RequestSourceSelectionAsync is null)
                        throw new InvalidOperationException("압축 안에 후보가 여러 개인데 선택 UI가 연결되어 있지 않습니다.");

                    string entryKey = await RequestSourceSelectionAsync(extractResult.Candidates)
                        ?? throw new OperationCanceledException();

                    actualSourcePath = await SourceArchiveExtractor.ExtractCandidateAsync(selectedSourcePath, extractDir, entryKey, BuildProgressReporter(), ct);
                }
                else
                    actualSourcePath = extractResult.ResolvedPath!;

                Log($"원본 압축 해제 완료: {Path.GetFileName(actualSourcePath)}", LogLevel.Ok);
            }

            extractDir ??= Path.Combine(outputDir, "_src_" + Path.GetFileNameWithoutExtension(actualSourcePath));
            Directory.CreateDirectory(extractDir);
            var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);
            bool directCompressedPatchApplied = false;
            DetectResult detected = new();
            bool sourceIsTemporary = false;

            string CreateOutputPath(string sourcePath)
            {
                string outputFileName = selectedSourceIsGdiTrack
                    ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRootDirectory)) + Path.GetExtension(sourcePath)
                    : Path.GetFileName(sourcePath);

                return Utils.GetUniqueFilePath(Path.Combine(outputDir, outputFileName));
            }

            outputPath = CreateOutputPath(actualSourcePath);

            directCompressedPatchApplied = await CompressedSourcePatchProbe.TryApplyAsync(
                actualSourcePath,
                actualPatchPath,
                outputPath,
                Log,
                BuildProgressReporter(),
                ct);

            if (directCompressedPatchApplied)
            {
                detected = FormatDetector.Detect(actualSourcePath);
                sourceIsTemporary = Path.GetFullPath(Path.GetDirectoryName(actualSourcePath)!)
                    .Equals(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase);

                await orchestrator.CompletePatchedFileAsync(actualSourcePath, detected, outputDir, outputPath, sourceIsTemporary, ct);
            }

            if (!directCompressedPatchApplied)
            {
                var (resolvedSourcePath, resolvedDetected) = await CompressedSourceDecompressor.ResolveAsync(actualSourcePath, extractDir, Log, BuildProgressReporter(), ct);

                actualSourcePath = resolvedSourcePath;
                detected = resolvedDetected;

                if (selectedSourceIsGdiTrack)
                {
                    detected = new DetectResult
                    {
                        Format = RomFormat.Gdi,
                        Direction = ConvertDirection.Compress,
                        OutputExtension = "chd"
                    };
                }

                sourceIsTemporary = Path.GetFullPath(Path.GetDirectoryName(actualSourcePath)!)
                    .Equals(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase);

                outputPath = CreateOutputPath(actualSourcePath);

                Log($"패치 시작: {Path.GetFileName(actualSourcePath)}", LogLevel.Highlight);

                await orchestrator.PatchAsync(actualSourcePath, actualPatchPath, detected, outputDir, outputPath, sourceIsTemporary, ct);
            }

            var generatedOutputEntries = ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBefore);

            if (existingElPackageRoot is not null)
            {
                var elPackageCreator = new ElPackageCreator(Log);

                try
                {
                    DirectoryPatchSourceResolver.UpdatePatchLog(Path.Combine(existingElPackageRoot, "_EL_CONFIG", "src", "patch.log"), selectedSourcePath);
                }
                catch (Exception ex)
                {
                    Log($"patch.log 기록 실패: {ex.Message}", LogLevel.Error);
                }

                elPackageCreator.InstallPackageResult(
                    existingElPackageRoot,
                    existingPackageSourcePaths!,
                    outputDir,
                    generatedOutputEntries,
                    Path.GetFileNameWithoutExtension(selectedSourcePath));
                completionDir = existingElPackageRoot;
            }
            else if (AppConfig.Instance.Patch.CreateElPackageWhenCompleted)
            {
                var originalPaths = sourceIsTemporary
                    ? new[] { selectedSourcePath }
                    : ElPackageCreator.ResolveSourcePaths(actualSourcePath, detected.Format);
                var elPackageCreator = new ElPackageCreator(Log);
                completionDir = elPackageCreator.Create(
                    selectedSourcePath,
                    originalPaths,
                    outputDir,
                    generatedOutputEntries,
                    selectedElPackageRoot);
                elPackageCreator.CleanupOutputDirectory(outputDir);
            }

            CleanupExtractDirectory(extractDir);
            extractDir = null;
            OutputDirectory = completionDir;

            stopwatch.Stop();

            Log($"패치 완료: {Path.GetFileName(outputPath)} ({stopwatch.Elapsed:mm\\:ss})", LogLevel.Ok);
            ProgressLabel = string.Empty;

            if (AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted)
                completionDir.OpenFolder();
            else
                PatchCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Log($"패치 취소: {SourcePath}", LogLevel.Error);
            CleanupTask();

            if (preserveFailedDcpOutput)
                Log($"DCP 진단을 위해 실패한 output 폴더를 유지합니다: {outputDir}", LogLevel.Highlight);
            else
            {
                if (outputPath is not null)
                    orchestrator.Cleanup(outputPath);

                CleanupFailedOutputDirectory(outputDir);
            }
        }
        catch (Exception ex)
        {
            Log($"패치 실패: {ex.Message}", LogLevel.Error);
            CleanupTask();

            if (preserveFailedDcpOutput)
                Log($"DCP 진단을 위해 실패한 output 폴더를 유지합니다: {outputDir}", LogLevel.Highlight);
            else
            {
                if (outputPath is not null)
                    orchestrator.Cleanup(outputPath);

                CleanupFailedOutputDirectory(outputDir);
            }
        }
        finally
        {
            if (patchArchiveExtraction is not null)
            {
                try
                {
                    PatchArchiveExtractor.Cleanup(patchArchiveExtraction);
                    Log("패치 압축파일 임시 파일 정리 완료", LogLevel.Ok);
                }
                catch (Exception ex)
                {
                    Log($"패치 압축파일 임시 파일 정리 실패: {ex.Message}", LogLevel.Error);
                }
            }

            CleanupExtractDirectory(extractDir);
        }
    }

    private void CleanupExtractDirectory(string? extractDir)
    {
        if (extractDir is null || !Directory.Exists(extractDir))
            return;

        try
        {
            Directory.Delete(extractDir, true);
            Log("원본 압축 해제 임시 파일 정리 완료", LogLevel.Ok);
        }
        catch (Exception ex)
        {
            Log($"압축 해제 임시 파일 정리 실패: {ex.Message}", LogLevel.Error);
        }
    }

    private Progress<ProgressInfo> BuildProgressReporter() =>
        new(info =>
        {
            ProgressPct = info.Percent;
            ProgressLabel = info.Label;
            ProgressPercent = $"{info.Percent}%";
            ProgressTime = info.TimeInfo;
            ProgressSpeed = info.Speed;
        });

    private async Task<(bool IsMultiDisc, string? SinglePatchPath)> ResolvePatchDirectoryModeAsync(string sourceInputDirectory, string patchDirectory, CancellationToken ct)
    {
        var location = DirectoryPatchSourceResolver.ResolveLocation(sourceInputDirectory);
        var multiDiscResolver = new MultiDiscPatchResolver(Log);
        var mode = await multiDiscResolver.AnalyzeAsync(location.SourceDirectory, patchDirectory, ct);

        if (mode.PatchPaths.Count == 0)
            throw new InvalidOperationException("패치 폴더 안에 지원하는 패치 파일이 없습니다.");

        if (mode.IsMultiDisc)
            return (true, null);

        if (mode.PatchPaths.Count == 1)
            return (false, mode.PatchPaths[0]);

        var sourceResolver = new DirectoryPatchSourceResolver((_, _) => { });

        foreach (string patchPath in mode.PatchPaths)
        {
            if (await sourceResolver.HasCompatibleSourceAsync(sourceInputDirectory, patchPath, ct))
                return (false, patchPath);
        }

        return (false, null);
    }

    private static async Task<string?> ResolvePatchForSingleSourceAsync(string sourcePath, string patchDirectory, CancellationToken ct)
    {
        var multiDiscResolver = new MultiDiscPatchResolver((_, _) => { });
        var sourceResolver = new DirectoryPatchSourceResolver((_, _) => { });

        foreach (string patchPath in await multiDiscResolver.GetSupportedPatchPathsAsync(patchDirectory, ct))
        {
            if (await sourceResolver.HasCompatibleSourceAsync(sourcePath, patchPath, ct))
                return patchPath;
        }

        return null;
    }

    private async Task RunMultiDiscAsync(string sourceInputDirectory, string patchDirectory, string outputFolderName, CancellationToken ct)
    {
        var location = DirectoryPatchSourceResolver.ResolveLocation(sourceInputDirectory);
        string? selectedPackageRoot = GetSelectedPackageRoot(sourceInputDirectory, location);
        string outputDir = Path.Combine(location.ExistingPackageRoot ?? selectedPackageRoot ?? sourceInputDirectory, outputFolderName);
        string workDirectory = Path.Combine(outputDir, "_multi_patch");
        OutputDirectory = outputDir;
        string completionDirectory = location.ExistingPackageRoot ?? selectedPackageRoot ?? sourceInputDirectory;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(workDirectory);

            var resolver = new MultiDiscPatchResolver(Log);

            if (MultiDiscPatchResolver.FindCuePaths(location.SourceDirectory).Count >= 2)
            {
                await RunMultiDiscTrackPatchSetsAsync(location, selectedPackageRoot, outputDir, workDirectory, patchDirectory, resolver, ct);
                return;
            }

            var pairs = await resolver.PrepareAsync(location.SourceDirectory, patchDirectory, workDirectory, ct);
            var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var outputEntriesByDisc = new List<IReadOnlyList<string>>();

            foreach (var pair in pairs
                .OrderBy(pair => GetDiscOrder(pair.SourcePath).Kind)
                .ThenBy(pair => GetDiscOrder(pair.SourcePath).Value)
                .ThenBy(pair => Path.GetFileName(pair.SourcePath), StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();

                var outputEntriesBeforeDisc = ElPackageCreator.CaptureOutputEntries(outputDir);
                var detected = RomForge.Core.Services.Compression.FormatDetector.Detect(pair.SourcePath);
                string outputPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, Path.GetFileName(pair.SourcePath)));
                File.Move(pair.PreparedOutputPath, outputPath);

                var orchestrator = new PatchOrchestrator(Log, BuildProgressReporter(), AutoCompress, AppConfig.Instance.Dolphin.CompressLevel);
                await orchestrator.CompletePatchedFileAsync(pair.SourcePath, detected, outputDir, outputPath, false, ct);

                foreach (var sourcePath in ElPackageCreator.ResolveSourcePaths(pair.SourcePath, detected.Format))
                    sourcePaths.Add(sourcePath);

                outputEntriesByDisc.Add(ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBeforeDisc));
            }

            var outputEntries = ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBefore);
            var m3uFiles = SelectM3uFiles(outputEntries).ToList();

            if (CreateElPackageWhenCompleted)
            {
                var elPackageCreator = new ElPackageCreator(Log);

                if (location.IsExistingElPackage)
                {
                    var existingSourcePaths = Directory.EnumerateFiles(location.SourceDirectory)
                        .Where(path => !string.Equals(Path.GetFileName(path), "patch.log", StringComparison.OrdinalIgnoreCase));
                    var targetOverrides = ElPackageCreator.CreateMultiDiscTargetOverrides(location.ExistingPackageRoot!, outputEntriesByDisc);

                    if (targetOverrides is null)
                    {
                        Log("멀티 디스크 기존 결과 이름을 1:1로 확인하지 못해 일반 결과 이름 처리를 사용합니다.", LogLevel.Info);
                    }
                    else
                    {
                        foreach (var target in targetOverrides)
                            Log($"멀티 디스크 결과 이름 유지: {Path.GetFileName(target.Key)} → {Path.GetFileName(target.Value.TargetPath)}", LogLevel.Highlight);
                    }

                    elPackageCreator.InstallPackageResult(location.ExistingPackageRoot!, existingSourcePaths, outputDir, outputEntries, targetOverrides: targetOverrides);
                    completionDirectory = location.ExistingPackageRoot!;
                }
                else
                {
                    completionDirectory = elPackageCreator.Create(pairs[0].SourcePath, sourcePaths, outputDir, outputEntries, selectedPackageRoot);
                    elPackageCreator.CleanupOutputDirectory(outputDir);
                }

                var m3uResult = M3uFileCreator.CreateIfMissing(completionDirectory, m3uFiles);
                Log(m3uResult.Created ? $"M3U 생성 완료: {m3uResult.Path}" : $"기존 M3U 유지: {m3uResult.Path}", LogLevel.Ok);

                if (Directory.Exists(outputDir))
                    elPackageCreator.CleanupOutputDirectory(outputDir);
            }

            stopwatch.Stop();
            Log($"멀티 디스크 패치 완료: {pairs.Count}개 디스크 ({stopwatch.Elapsed:mm\\:ss})", LogLevel.Ok);
            ProgressLabel = string.Empty;

            OutputDirectory = CreateElPackageWhenCompleted ? completionDirectory : outputDir;
            if (AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted)
                completionDirectory.OpenFolder();
            else
                PatchCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            CleanupWorkDirectory(workDirectory);
            CleanupFailedOutputDirectory(outputDir);
            throw;
        }
        finally
        {
            CleanupWorkDirectory(workDirectory);
        }
    }

    private async Task RunMultiDiscTrackPatchSetsAsync(
        DirectoryPatchLocation location,
        string? selectedPackageRoot,
        string outputDir,
        string workDirectory,
        string patchDirectory,
        MultiDiscPatchResolver resolver,
        CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var pairs = await resolver.PreparePatchSetAsync(location.SourceDirectory, patchDirectory, workDirectory, ct);
        var discPatchSets = MultiDiscPatchResolver.CreateDiscPatchSets(pairs);

        if (discPatchSets.Count < 2 || discPatchSets.Sum(set => set.Pairs.Count) != pairs.Count)
            throw new InvalidOperationException("모든 멀티 디스크 패치를 CUE 디스크에 매칭하지 못했습니다.");

        var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputEntriesByDisc = new List<IReadOnlyList<string>>();

        foreach (var discPatchSet in discPatchSets
            .OrderBy(set => GetDiscOrder(set.CuePath).Kind)
            .ThenBy(set => GetDiscOrder(set.CuePath).Value)
            .ThenBy(set => Path.GetFileName(set.CuePath), StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();

            var outputEntriesBeforeDisc = ElPackageCreator.CaptureOutputEntries(outputDir);
            string stagingDirectory = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
            string stagingCuePath = Path.Combine(stagingDirectory, Path.GetFileName(discPatchSet.CuePath));
            var discSourcePaths = new[] { discPatchSet.CuePath }
                .Concat(CHD.Core.Services.ConversionSource.ParseBinsFromCue(discPatchSet.CuePath))
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Directory.CreateDirectory(stagingDirectory);

            foreach (string sourcePath in discSourcePaths)
                File.Copy(sourcePath, Path.Combine(stagingDirectory, Path.GetFileName(sourcePath)), true);

            foreach (var pair in discPatchSet.Pairs)
                File.Move(pair.PreparedOutputPath, Path.Combine(stagingDirectory, Path.GetFileName(pair.SourcePath)), true);

            if (AutoCompress)
            {
                using var converter = new CHD.Core.Services.FileConverter(AppConfig.Instance.Chdman.Compression);
                converter.LogMessage += (_, e) => Log(e.Message, e.Level);

                var conversion = await converter.ConvertFileAsync(stagingCuePath, null, BuildProgressReporter(), ct);

                if (!conversion.Success || conversion.OutputFile is null)
                    throw new InvalidOperationException($"CHD 변환 실패: {conversion.Message}");

                string chdPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(discPatchSet.CuePath), ".chd")));
                File.Move(conversion.OutputFile, chdPath);
                Directory.Delete(stagingDirectory, true);
            }
            else
            {
                string resultDirectory = Utils.GetUniqueFolderPath(Path.Combine(outputDir, Path.GetFileNameWithoutExtension(discPatchSet.CuePath)));
                Directory.Move(stagingDirectory, resultDirectory);
            }

            foreach (string sourcePath in discSourcePaths)
                sourcePaths.Add(sourcePath);

            outputEntriesByDisc.Add(ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBeforeDisc));
            Log($"디스크 구성 패치 완료: {Path.GetFileName(discPatchSet.CuePath)} ({discPatchSet.Pairs.Count}개 트랙)", LogLevel.Ok);
        }

        var outputEntries = ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBefore);
        var m3uFiles = SelectM3uFiles(outputEntries).ToList();
        string completionDirectory = location.ExistingPackageRoot ?? selectedPackageRoot ?? location.SourceDirectory;

        if (CreateElPackageWhenCompleted)
        {
            var elPackageCreator = new ElPackageCreator(Log);

            if (location.IsExistingElPackage)
            {
                var existingSourcePaths = Directory.EnumerateFiles(location.SourceDirectory)
                    .Where(path => !string.Equals(Path.GetFileName(path), "patch.log", StringComparison.OrdinalIgnoreCase));
                var targetOverrides = ElPackageCreator.CreateMultiDiscTargetOverrides(location.ExistingPackageRoot!, outputEntriesByDisc);

                elPackageCreator.InstallPackageResult(location.ExistingPackageRoot!, existingSourcePaths, outputDir, outputEntries, targetOverrides: targetOverrides);
                completionDirectory = location.ExistingPackageRoot!;
            }
            else
            {
                completionDirectory = elPackageCreator.Create(discPatchSets[0].CuePath, sourcePaths, outputDir, outputEntries, selectedPackageRoot);
                elPackageCreator.CleanupOutputDirectory(outputDir);
            }

            var m3uResult = M3uFileCreator.CreateIfMissing(completionDirectory, m3uFiles);
            Log(m3uResult.Created ? $"M3U 생성 완료: {m3uResult.Path}" : $"기존 M3U 유지: {m3uResult.Path}", LogLevel.Ok);

            if (Directory.Exists(outputDir))
                elPackageCreator.CleanupOutputDirectory(outputDir);
        }

        stopwatch.Stop();
        Log($"멀티 디스크 패치 완료: {discPatchSets.Count}개 디스크 ({stopwatch.Elapsed:mm\\:ss})", LogLevel.Ok);
        ProgressLabel = string.Empty;

        OutputDirectory = CreateElPackageWhenCompleted ? completionDirectory : outputDir;
        if (AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted)
            completionDirectory.OpenFolder();
        else
            PatchCompleted?.Invoke(this, EventArgs.Empty);
    }

    private async Task<bool> TryRunSingleDiscPatchSetAsync(string sourceInputDirectory, string patchDirectory, string outputFolderName, CancellationToken ct)
    {
        var location = DirectoryPatchSourceResolver.ResolveLocation(sourceInputDirectory);
        string? sourceDirectory = MultiDiscPatchResolver.FindSingleCueSourceDirectory(location.SourceDirectory);

        if (sourceDirectory is null)
            return false;

        string? selectedPackageRoot = GetSelectedPackageRoot(sourceInputDirectory, location);
        string outputDir = Path.Combine(location.ExistingPackageRoot ?? selectedPackageRoot ?? sourceDirectory, outputFolderName);
        string workDirectory = Path.Combine(outputDir, "_single_disc_patch");
        OutputDirectory = outputDir;
        var resolver = new MultiDiscPatchResolver(Log);
        IReadOnlyList<MultiDiscPatchPair> pairs = [];

        try
        {
            Directory.CreateDirectory(workDirectory);
            pairs = await resolver.PreparePatchSetAsync(sourceDirectory, patchDirectory, workDirectory, ct);
            var patchSet = MultiDiscPatchResolver.TryCreateSingleDiscPatchSet(pairs);

            if (patchSet is null || patchSet.Pairs.Count < 2)
            {
                foreach (var pair in pairs)
                    if (File.Exists(pair.PreparedOutputPath))
                        File.Delete(pair.PreparedOutputPath);

                return false;
            }

            await RunSingleDiscPatchSetAsync(location, sourceDirectory, outputDir, workDirectory, patchSet, selectedPackageRoot, ct);
            return true;
        }
        finally
        {
            CleanupWorkDirectory(workDirectory);
        }
    }

    private async Task RunSingleDiscPatchSetAsync(
        DirectoryPatchLocation location,
        string sourceDirectory,
        string outputDir,
        string workDirectory,
        SingleDiscPatchSet patchSet,
        string? selectedPackageRoot,
        CancellationToken ct)
    {
        string stagingDirectory = Path.Combine(workDirectory, "disc");
        string stagingCuePath = Path.Combine(stagingDirectory, Path.GetFileName(patchSet.CuePath));
        string completionDirectory = location.ExistingPackageRoot ?? selectedPackageRoot ?? sourceDirectory;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);
            var discSourcePaths = new[] { patchSet.CuePath }
                .Concat(CHD.Core.Services.ConversionSource.ParseBinsFromCue(patchSet.CuePath))
                .Where(File.Exists)
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var originalPaths = discSourcePaths
                .Concat(patchSet.AuxiliaryPairs.Select(pair => pair.SourcePath))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            Directory.CreateDirectory(stagingDirectory);

            foreach (string sourcePath in discSourcePaths)
                File.Copy(sourcePath, Path.Combine(stagingDirectory, Path.GetFileName(sourcePath)), true);

            foreach (var pair in patchSet.DiscPairs)
            {
                string targetPath = Path.Combine(stagingDirectory, Path.GetFileName(pair.SourcePath));
                File.Move(pair.PreparedOutputPath, targetPath, true);
            }

            if (AutoCompress)
            {
                using var converter = new CHD.Core.Services.FileConverter(AppConfig.Instance.Chdman.Compression);
                converter.LogMessage += (_, e) => Log(e.Message, e.Level);

                var conversion = await converter.ConvertFileAsync(stagingCuePath, null, BuildProgressReporter(), ct);

                if (!conversion.Success || conversion.OutputFile is null)
                    throw new InvalidOperationException($"CHD 변환 실패: {conversion.Message}");

                string chdPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, Path.ChangeExtension(Path.GetFileName(patchSet.CuePath), ".chd")));
                File.Move(conversion.OutputFile, chdPath);
                Directory.Delete(stagingDirectory, true);

                foreach (var pair in patchSet.AuxiliaryPairs)
                    File.Move(pair.PreparedOutputPath, Path.Combine(outputDir, Path.GetFileName(pair.SourcePath)), true);
            }
            else
            {
                string resultDirectory = Utils.GetUniqueFolderPath(Path.Combine(outputDir, Path.GetFileNameWithoutExtension(patchSet.CuePath)));
                Directory.Move(stagingDirectory, resultDirectory);

                foreach (var pair in patchSet.AuxiliaryPairs)
                    File.Move(pair.PreparedOutputPath, Path.Combine(resultDirectory, Path.GetFileName(pair.SourcePath)), true);
            }

            var generatedOutputEntries = ElPackageCreator.GetNewOutputEntries(outputDir, outputEntriesBefore);

            if (CreateElPackageWhenCompleted)
            {
                var elPackageCreator = new ElPackageCreator(Log);

                if (location.IsExistingElPackage)
                {
                    var existingSourcePaths = Directory.EnumerateFiles(location.SourceDirectory)
                        .Where(path => !string.Equals(Path.GetFileName(path), "patch.log", StringComparison.OrdinalIgnoreCase));

                    DirectoryPatchSourceResolver.UpdatePatchLog(
                        Path.Combine(location.SourceDirectory, "patch.log"),
                        patchSet.CuePath);
                    elPackageCreator.InstallPackageResult(location.ExistingPackageRoot!, existingSourcePaths, outputDir, generatedOutputEntries);
                    completionDirectory = location.ExistingPackageRoot!;
                }
                else
                {
                    completionDirectory = elPackageCreator.Create(patchSet.CuePath, originalPaths, outputDir, generatedOutputEntries, selectedPackageRoot);
                    elPackageCreator.CleanupOutputDirectory(outputDir);
                }
            }

            stopwatch.Stop();
            Log($"단일 디스크 다중 파일 패치 완료: {patchSet.Pairs.Count}개 파일 ({stopwatch.Elapsed:mm\\:ss})", LogLevel.Ok);
            ProgressLabel = string.Empty;

            OutputDirectory = CreateElPackageWhenCompleted ? completionDirectory : outputDir;
            if (AppConfig.Instance.Patch.OpenOutputFolderWhenCompleted)
                completionDirectory.OpenFolder();
            else
                PatchCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            CleanupFailedOutputDirectory(outputDir);
            throw;
        }
    }

    private static IEnumerable<string> SelectM3uFiles(IEnumerable<string> outputEntries)
    {
        var files = outputEntries.Where(File.Exists).ToList();
        var chdFiles = files.Where(path => Path.GetExtension(path).Equals(".chd", StringComparison.OrdinalIgnoreCase)).ToList();
        if (chdFiles.Count > 0)
            return chdFiles;

        var cueFiles = files.Where(path => Path.GetExtension(path).Equals(".cue", StringComparison.OrdinalIgnoreCase)).ToList();
        return cueFiles.Count > 0 ? cueFiles : files;
    }

    private static (int Kind, int Value) GetDiscOrder(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"\b(?:disc|disk)\s*(\d+|[a-z])\b", RegexOptions.IgnoreCase);

        if (!match.Success)
            return (2, int.MaxValue);

        string value = match.Groups[1].Value;

        return int.TryParse(value, out int number)
            ? (0, number)
            : (1, char.ToUpperInvariant(value[0]) - 'A');
    }

    private bool ShouldUseSelectedDirectoryAsElPackageRoot(string sourceInputDirectory, DirectoryPatchLocation location) =>
        CreateElPackageWhenCompleted
        && !location.IsExistingElPackage
        && !Directory.EnumerateFiles(sourceInputDirectory, "*", SearchOption.TopDirectoryOnly).Any();

    private string? GetSelectedPackageRoot(string sourceInputDirectory, DirectoryPatchLocation location) =>
        ShouldUseSelectedDirectoryAsElPackageRoot(sourceInputDirectory, location)
            ? sourceInputDirectory
            : null;

    private void CleanupTask()
    {
        ProgressPct = 0;
        ProgressLabel = string.Empty;
        ProgressPercent = "0%";
        ProgressTime = string.Empty;
        ProgressSpeed = string.Empty;
    }

    private void ShowSourceSearchWarning()
    {
        const string message = "패치에 사용할 원본 파일을 찾을 수 없습니다. _EL_CONFIG/src의 patch.log 또는 원본 파일을 확인해주세요.";
        Log(message, LogLevel.Error);
        NSW.WPF.UI.MessageBoxHelper.ShowWarning(message);
    }

    private void CleanupFailedOutputDirectory(string outputDir)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(outputDir))
                    return;

                Directory.Delete(outputDir, true);
                Log("오류 발생 후 output 폴더 정리 완료", LogLevel.Ok);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
            catch (Exception ex)
            {
                Log($"오류 발생 후 output 폴더 정리 실패: {ex.Message}", LogLevel.Error);
                return;
            }
        }
    }

    private void CleanupWorkDirectory(string workDirectory)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (!Directory.Exists(workDirectory))
                    return;

                Directory.Delete(workDirectory, true);
                return;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(250 * (attempt + 1)));
            }
            catch (Exception ex)
            {
                Log($"멀티 디스크 임시 폴더 정리 실패: {ex.Message}", LogLevel.Error);
                return;
            }
        }

        Log($"멀티 디스크 임시 폴더 정리 실패: {workDirectory}", LogLevel.Error);
    }

    private static string? GetElPackageRoot(string sourcePath)
    {
        string? sourceDirectory = Path.GetDirectoryName(sourcePath);
        string? configDirectory = sourceDirectory is null ? null : Path.GetDirectoryName(sourceDirectory);

        return string.Equals(Path.GetFileName(sourceDirectory), "src", StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(configDirectory), "_EL_CONFIG", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(configDirectory)
            : null;
    }

    public void Cancel() => _runCts?.Cancel();

    public void Clear()
    {
        _runCts?.Cancel();

        SourcePath = null;
        PatchPath = null;
        AutoCompress = false;
        CreateElPackageWhenCompleted = false;

        CleanupTask();

        LogEntries.Clear();
    }
}
