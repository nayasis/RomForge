using Common;
using Common.WPF.ViewModels;
using NSW.WPF.Services;
using RomForge.Core;
using RomForge.Core.Models;
using RomForge.Core.Services;
using RomForge.Core.Models.Patch;
using RomForge.Core.Services.Patch;
using System.Diagnostics;
using System.IO;
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
            }
        };

        AppConfig.Instance.Patch.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PatchConfig.AutoCompress))
                OnPropertyChanged(nameof(AutoCompress));

            if (e.PropertyName == nameof(PatchConfig.CreateElPackageWhenCompleted))
                OnPropertyChanged(nameof(CreateElPackageWhenCompleted));
        };
    }

    public void Log(string message, LogLevel level)
    {
        Application.Current?.Dispatcher?.Invoke(() => LogEntries.Add(new LogEntry { Message = message, Level = level }));
    }

    public async Task RunAsync()
    {
        if (SourcePath is null || PatchPath is null)
            return;

        _runCts = new CancellationTokenSource();

        var ct = _runCts.Token;
        bool sourceIsDirectory = Directory.Exists(SourcePath);
        bool patchIsDirectory = Directory.Exists(PatchPath);
        string sourceRootDirectory = sourceIsDirectory ? SourcePath : Path.GetDirectoryName(SourcePath)!;
        string outputDir = Path.Combine(sourceRootDirectory, "output");
        string? extractDir = null;
        string? outputPath = null;
        string completionDir = outputDir;
        string selectedSourcePath = SourcePath;
        string? existingElPackageRoot = !sourceIsDirectory && AppConfig.Instance.Patch.CreateElPackageWhenCompleted
            ? GetElPackageRoot(selectedSourcePath)
            : null;
        IReadOnlyList<string>? existingPackageSourcePaths = null;
        bool preserveFailedDcpOutput = Path.GetExtension(PatchPath).Equals(".dcp", StringComparison.OrdinalIgnoreCase);

        var orchestrator = new PatchOrchestrator(Log, BuildProgressReporter(), AutoCompress, AppConfig.Instance.Dolphin.CompressLevel);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (patchIsDirectory)
            {
                if (!sourceIsDirectory)
                    throw new InvalidOperationException("멀티 디스크 패치는 원본과 패치를 모두 디렉토리로 입력해야 합니다.");

                await RunMultiDiscAsync(SourcePath, PatchPath, ct);
                return;
            }

            if (sourceIsDirectory)
            {
                var directorySource = await new DirectoryPatchSourceResolver(Log).ResolveAsync(SourcePath, PatchPath, ct);

                if (directorySource is null)
                {
                    ShowSourceSearchWarning();
                    return;
                }

                selectedSourcePath = directorySource.SourcePath;
                existingElPackageRoot = directorySource.IsExistingElPackage && AppConfig.Instance.Patch.CreateElPackageWhenCompleted
                    ? directorySource.ExistingPackageRoot
                    : null;
            }

            if (existingElPackageRoot is not null)
            {
                string sourceDirectory = Path.Combine(existingElPackageRoot, "_EL_CONFIG", "src");
                existingPackageSourcePaths = [.. Directory.EnumerateFiles(sourceDirectory)
                    .Where(path => !string.Equals(Path.GetFileName(path), "patch.log", StringComparison.OrdinalIgnoreCase))];
                outputDir = Path.Combine(existingElPackageRoot, "output");
                completionDir = outputDir;
            }

            Directory.CreateDirectory(outputDir);

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

            var (resolvedSourcePath, detected) = await CompressedSourceDecompressor.ResolveAsync(actualSourcePath, extractDir, Log, BuildProgressReporter(), ct);

            actualSourcePath = resolvedSourcePath;

            bool sourceIsTemporary = Path.GetFullPath(Path.GetDirectoryName(actualSourcePath)!)
                .Equals(Path.GetFullPath(extractDir), StringComparison.OrdinalIgnoreCase);

            outputPath = Path.Combine(outputDir, Path.GetFileName(actualSourcePath));
            outputPath = Utils.GetUniqueFilePath(outputPath);
            var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);

            Log($"패치 시작: {Path.GetFileName(actualSourcePath)}", LogLevel.Highlight);

            await orchestrator.PatchAsync(actualSourcePath, PatchPath, detected, outputDir, outputPath, sourceIsTemporary, ct);

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

                elPackageCreator.InstallPackageResult(existingElPackageRoot, existingPackageSourcePaths!, outputDir, generatedOutputEntries);
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
                    generatedOutputEntries);
                elPackageCreator.CleanupOutputDirectory(outputDir);
            }

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
            if (extractDir is not null && Directory.Exists(extractDir))
            {
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

    private async Task RunMultiDiscAsync(string sourceInputDirectory, string patchDirectory, CancellationToken ct)
    {
        var location = DirectoryPatchSourceResolver.ResolveLocation(sourceInputDirectory);
        string outputDir = Path.Combine(location.ExistingPackageRoot ?? sourceInputDirectory, "output");
        string workDirectory = Path.Combine(outputDir, "_multi_patch");
        string completionDirectory = location.ExistingPackageRoot ?? sourceInputDirectory;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            Directory.CreateDirectory(outputDir);
            Directory.CreateDirectory(workDirectory);

            var resolver = new MultiDiscPatchResolver(Log);
            var pairs = await resolver.PrepareAsync(location.SourceDirectory, patchDirectory, workDirectory, ct);
            var outputEntriesBefore = ElPackageCreator.CaptureOutputEntries(outputDir);
            var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in pairs)
            {
                ct.ThrowIfCancellationRequested();

                var detected = RomForge.Core.Services.Compression.FormatDetector.Detect(pair.SourcePath);
                string outputPath = Utils.GetUniqueFilePath(Path.Combine(outputDir, Path.GetFileName(pair.SourcePath)));
                File.Move(pair.PreparedOutputPath, outputPath);

                var orchestrator = new PatchOrchestrator(Log, BuildProgressReporter(), AutoCompress, AppConfig.Instance.Dolphin.CompressLevel);
                await orchestrator.CompletePatchedFileAsync(pair.SourcePath, detected, outputDir, outputPath, false, ct);

                foreach (var sourcePath in ElPackageCreator.ResolveSourcePaths(pair.SourcePath, detected.Format))
                    sourcePaths.Add(sourcePath);
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
                    elPackageCreator.InstallPackageResult(location.ExistingPackageRoot!, existingSourcePaths, outputDir, outputEntries);
                    completionDirectory = location.ExistingPackageRoot!;
                }
                else
                {
                    completionDirectory = elPackageCreator.Create(pairs[0].SourcePath, sourcePaths, outputDir, outputEntries);
                    elPackageCreator.CleanupOutputDirectory(outputDir);
                }

                var m3uResult = M3uFileCreator.CreateIfMissing(completionDirectory, m3uFiles);
                Log(m3uResult.Created ? $"M3U 생성 완료: {m3uResult.Path}" : $"기존 M3U 유지: {m3uResult.Path}", LogLevel.Ok);
            }

            stopwatch.Stop();
            Log($"멀티 디스크 패치 완료: {pairs.Count}개 디스크 ({stopwatch.Elapsed:mm\\:ss})", LogLevel.Ok);
            ProgressLabel = string.Empty;

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
        finally
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, true);
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
        MessageBox.Show(message, "원본 파일 검색 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CleanupFailedOutputDirectory(string outputDir)
    {
        try
        {
            if (Directory.Exists(outputDir))
                Directory.Delete(outputDir, true);

            Log("오류 발생 후 output 폴더 정리 완료", LogLevel.Ok);
        }
        catch (Exception ex)
        {
            Log($"오류 발생 후 output 폴더 정리 실패: {ex.Message}", LogLevel.Error);
        }
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
