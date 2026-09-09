using _3DS.Core.Crypto;
using _3DS.Core.Models;
using _3DS.Core.Services;
using CD.Core.Services.Readers;
using CD.Core.Services.Writers;
using Common;
using Common.WPF.ViewModels;
using PBP.Core.Enums;
using PBP.Core.Services;
using RomForge.Core;
using RomForge.Core.Models;
using RomForge.Core.Models._3DS;
using RomForge.Core.Models.CD;
using RomForge.Core.Models.PS;
using RomForge.Core.Models.Switch;
using RomForge.Core.Models.WiiU;
using RomForge.Core.Services.PS;
using RomForge.Core.Services.Switch;
using RomForge.Core.Services.WiiU;
using RomForge.Core.UI.Command;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WiiU.Core.Services;

namespace RomForge.ViewModels;

public class ConvertMainViewModel : ToolTabViewModel
{
    private CancellationTokenSource _cts = new();

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nsp", ".xci",
        ".cci", ".cia", ".3ds",
        ".wud", ".wux", ".wua",
        ".mds", ".ccd",
        ".pbp",
    };

    private static string KeysPath => Path.Combine(AppConfig.ApplicationDirectory, "keys.txt");

    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public ObservableCollection<object> FileItems { get; } = [];

    public Visibility HintVisibility => FileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand RunCommand { get; }

    public event Action<object>? ScrollToItemRequested;
    public event EventHandler? RunNavigateCerts;

    public ConvertMainViewModel()
    {
        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsLocked && FileItems.Count > 0);
        CancelCommand = new RelayCommand(_ => _cts.Cancel(), _ => IsLocked);
    }

    public async Task AddPaths(IEnumerable<string> paths)
    {
        var existing = FileItems
            .Select(GetFilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newItems = new List<object>();

        foreach (var path in ExpandPaths(paths))
        {
            if (!existing.Add(path))
                continue;

            var item = CreateItem(path);

            if (item is null)
                continue;

            FileItems.Add(item);
            newItems.Add(item);
        }

        Renumber();
        OnPropertyChanged(nameof(HintVisibility));
        CommandManager.InvalidateRequerySuggested();

        foreach (var item in newItems)
            await ProbeMetadataAsync(item);
    }

    public void RemoveItems(IEnumerable<object> items)
    {
        foreach (var item in items.ToList())
            FileItems.Remove(item);

        Renumber();
        OnPropertyChanged(nameof(HintVisibility));
    }

    public void ClearItems()
    {
        FileItems.Clear();
        OnPropertyChanged(nameof(HintVisibility));
    }

    private void Renumber()
    {
        for (int i = 0; i < FileItems.Count; i++)
            ((IProgressTrackable)FileItems[i]).No = i + 1;
    }

    private static string GetFilePath(object item) => item switch
    {
        FileItemBase f => f.FilePath,
        _ => string.Empty
    };

    private static object? CreateItem(string path)
    {
        var ext = Directory.Exists(path) ? "" : Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

        switch (ext)
        {
            case "nsp":
            case "xci":
                var sw = new ConverterFileItem(path);
                FilterFormats(sw, "XCI", "NSP");
                return sw.SelectedTargetFormat == "" ? null : sw;

            case "cci":
            case "3ds":
            case "cia":
                var ds = new _3DSFileItem(path);
                FilterFormats(ds, "CIA", "CCI");
                return ds.SelectedTargetFormat == "" ? null : ds;

            case "wud":
            case "wux":
            case "wua":
                return new WiiUFileItem(path);

            case "mds":
            case "ccd":
                return new CdConvertFileItem(path);

            case "pbp":
                return new PbpFileItem(path);

            default:
                if (Directory.Exists(path))
                {
                    var wiiu = new WiiUFileItem(path);
                    return wiiu.SelectedTargetFormat == "미지원" ? null : wiiu;
                }

                return null;
        }
    }

    private static void FilterFormats(Common.WPF.ViewModels.IConvertible item, params string[] allowed)
    {
        var kept = item.AvailableFormats.Where(f => allowed.Contains(f, StringComparer.OrdinalIgnoreCase)).ToList();

        item.AvailableFormats.Clear();
        item.AvailableFormats.AddRange(kept);
        item.SelectedTargetFormat = kept.FirstOrDefault() ?? "";
    }

    private static async Task ProbeMetadataAsync(object item)
    {
        try
        {
            switch (item)
            {
                case _3DSFileItem ds:
                    {
                        var result = await Task.Run(() => Core.Services._3DS.Util.ParseFile(ds.FilePath));

                        ds.TitleId = result.Title!.TitleId;
                        ds.ProductCode = result.ProductCode;
                        ds.ShortDescription = result.ShortDescription;
                        ds.Publisher = result.Publisher;
                        ds.Crypto = result.Crypto;

                        if (result.IconPixels is not null)
                        {
                            var bitmap = BitmapSource.Create(48, 48, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, result.IconPixels, 48 * 4);
                            bitmap.Freeze();
                            ds.Icon = bitmap;
                        }
                    }
                    break;

                case WiiUFileItem wu:
                    await Task.Run(() =>
                    {
                        if (Directory.Exists(wu.FilePath))
                        {
                            using ITitleSource source = wu.Extension == "wup"
                                ? new WupTitleSource(wu.FilePath)
                                : new FolderTitleSource(wu.FilePath);

                            wu.TitleIdHex = source.TitleIdHex;
                            wu.TitleVersion = source.TitleVersion;

                            var folderMeta = wu.Extension == "wup"
                                ? WiiUMetadataExtractor.ExtractFromTitleSource(source)
                                : WiiUMetadataExtractor.ExtractFromFolder(wu.FilePath);

                            if (folderMeta is not null)
                                wu.TitleName = folderMeta.Title;
                        }
                    });

                    if (!Directory.Exists(wu.FilePath))
                    {
                        var meta = await WiiUMetadataExtractor.Extract(wu.FilePath, KeysPath);

                        if (meta is not null)
                            wu.TitleName = meta.Title;
                    }

                    break;

                case CdConvertFileItem cd:
                    var trackCount = await Task.Run(() =>
                    {
                        var reader = DiscImageReaderFactory.Resolve(cd.FilePath);
                        return reader.Read(cd.FilePath).TrackCount;
                    });

                    cd.TrackCount = trackCount;
                    break;

                case PbpFileItem pbp:
                    await Task.Run(() =>
                    {
                        using var stream = new FileStream(pbp.FilePath, FileMode.Open, FileAccess.Read);
                        var reader = new PbpReader(stream);
                        var meta = GameMetadataLookup.Find(reader.Discs[0].DiscID);

                        pbp.TitleName = meta?.ETitle ?? string.Empty;
                        pbp.TitleLocalName = meta?.LTitle ?? string.Empty;
                        pbp.Languages = meta?.Languages ?? [];
                        pbp.TitleId = string.Join(", ", reader.Discs.Select(d => d.DiscID));

                        if (PbpReader.TryGetResourceStream(ResourceType.ICON0, stream, out var iconStream))
                        {
                            var bitmap = new BitmapImage();

                            bitmap.BeginInit();
                            bitmap.StreamSource = iconStream;
                            bitmap.CacheOption = BitmapCacheOption.OnLoad;
                            bitmap.EndInit();
                            bitmap.Freeze();

                            pbp.Icon = bitmap;
                        }
                    });
                    break;
            }
        }
        catch
        {
        }
    }

    private async Task RunAsync()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();
        ClearLog();

        using (BeginWork())
        {
            try
            {
                int cnt = 0;

                AppendLog($"총 {FileItems.Count}개의 통합 변환 작업을 시작합니다.", LogLevel.Highlight);

                foreach (var item in FileItems.ToList())
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var tracker = (IProgressTrackable)item;

                    if (tracker.Status is "완료" or "미지원")
                        continue;

                    tracker.Status = "대기중";
                    tracker.Progress = 0;
                    tracker.Status = "변환중";

                    ScrollToItemRequested?.Invoke(item);

                    try
                    {
                        await ConvertOneAsync(item);

                        tracker.Progress = 100;
                        tracker.Status = "완료";
                        cnt++;
                    }
                    catch (CertsBinNotFoundException e)
                    {
                        AppendLog(e.Message, LogLevel.Error);
                        RunNavigateCerts?.Invoke(this, EventArgs.Empty);
                        tracker.Status = "실패";
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        AppendLog($"[{GetDisplayName(item)}] 변환 실패: {ex.Message}", LogLevel.Error);
                        tracker.Status = "실패";
                        tracker.Progress = 0;
                    }
                }

                AppendLog(cnt > 0 ? $"총 {cnt}개의 작업을 성공적으로 완료했습니다." : "성공한 작업이 없습니다.", cnt > 0 ? LogLevel.Ok : LogLevel.Error);
            }
            catch (OperationCanceledException)
            {
                AppendLog("작업이 취소되었습니다.", LogLevel.Error);

                foreach (var item in FileItems)
                {
                    var tracker = (IProgressTrackable)item;

                    if (tracker.Status is "대기중" or "변환중")
                        tracker.Status = "취소";
                }
            }
            finally
            {
            }
        }
    }

    private async Task ConvertOneAsync(object item)
    {
        var progress = new Progress<ProgressInfo>(p => ((IProgressTrackable)item).Progress = p.Percent);
        void Log(string msg, LogLevel level, string id = "") => AppendLog(msg, level);

        switch (item)
        {
            case ConverterFileItem sw:
                {
                    switch (sw.Extension.ToLowerInvariant(), sw.SelectedTargetFormat.ToUpperInvariant())
                    {
                        case ("nsp", "XCI"):
                            await NspXciConvertService.NspToXciAsync(sw.FilePath, progress, Log, _cts.Token);
                            break;
                        case ("xci", "NSP"):
                            await NspXciConvertService.XciToNspAsync(sw.FilePath, progress, Log, _cts.Token);
                            break;
                        default:
                            throw new NotSupportedException($"{sw.Extension} → {sw.SelectedTargetFormat}: 지원하지 않는 변환입니다.");
                    }
                }
                break;

            case _3DSFileItem ds:
                {
                    KeyStore key = new();

                    switch (ds.Extension.ToLowerInvariant(), ds.SelectedTargetFormat.ToUpperInvariant())
                    {
                        case ("cci", "CIA") or ("3ds", "CIA"):
                            await new CciToCiaConverter(key).ConvertAsync(ds.FilePath, progress, AppendLog, _cts.Token);
                            break;
                        case ("cia", "CCI"):
                            await new CiaToCciConverter(key).ConvertAsync(ds.FilePath, progress, AppendLog, _cts.Token);
                            break;
                        default:
                            throw new NotSupportedException($"{ds.Extension} → {ds.SelectedTargetFormat}: 지원하지 않는 변환입니다.");
                    }
                }
                break;

            case WiiUFileItem wu:
                await Task.Run(() => ConvertWiiUOne(wu, _cts.Token));
                break;

            case CdConvertFileItem cd:
                {
                    var reader = DiscImageReaderFactory.Resolve(cd.FilePath);
                    var discImage = reader.Read(cd.FilePath);
                    var outDir = ResolveOutputDir(cd.FilePath);

                    if (cd.OutputFormat == CdOutputFormat.Iso)
                        await IsoWriter.WriteAsync(discImage, outDir, cd.FileName, progress, _cts.Token);
                    else
                        await BinCueWriter.WriteAsync(discImage, outDir, cd.FileName, progress, _cts.Token);
                }
                break;

            case PbpFileItem pbp:
                {
                    var unpacker = new PbpUnpacker
                    {
                        OnNotify = msg => AppendLog(msg),
                        OnProgress = percent => ((IProgressTrackable)pbp).Progress = percent
                    };

                    await unpacker.UnpackAsync(pbp.FilePath, ResolveOutputDir(pbp.FilePath), true, _cts.Token);
                }
                break;
        }
    }

    private void ConvertWiiUOne(WiiUFileItem item, CancellationToken ct)
    {
        var sources = WiiUConverter.OpenSources(item.FilePath, KeysPath);
        var outputRoot = ResolveOutputDir(item.FilePath);

        try
        {
            int total = sources.Count;

            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var source = sources[i];
                var outputName = WiiUConverter.BuildOutputName(source, item.TitleName);

                void OnFileProgress(int done, int totalFiles, string label)
                {
                    int subPercent = totalFiles > 0 ? (int)(done * 100.0 / totalFiles) : 100;
                    item.Progress = (int)(((i * 100.0) + subPercent) / total);
                }

                switch (item.SelectedTargetFormat)
                {
                    case "WUP":
                        var wupFolder = Utils.GetUniqueFolderPath(Path.Combine(outputRoot, $"{outputName} [WUP]"));
                        WiiUConverter.ConvertToWup(source, wupFolder, OnFileProgress, ct);
                        break;

                    case "Loadiine":
                        var loadiineFolder = Utils.GetUniqueFolderPath(Path.Combine(outputRoot, $"{outputName} [Loadiine]"));
                        WiiUConverter.ConvertToLoadiine(source, loadiineFolder, OnFileProgress, ct);
                        break;

                    case "WUA":
                        var wuaFile = Utils.GetUniqueFilePath(Path.Combine(outputRoot, $"{outputName}.wua"));
                        WiiUConverter.ConvertToWua(source, wuaFile, OnFileProgress, ct);
                        break;

                    default:
                        throw new NotSupportedException($"지원하지 않는 출력 포맷입니다: {item.SelectedTargetFormat}");
                }
            }
        }
        finally
        {
            foreach (var s in sources)
                s.Dispose();
        }
    }    
    private static string ResolveOutputDir(string sourcePath) => Path.GetDirectoryName(sourcePath)!;

    private static string GetDisplayName(object item) => item switch
    {
        FileItemBase f => f.FileName,
        _ => "알 수 없음"
    };

    private static IEnumerable<string> ExpandPaths(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                if (WupTitleSource.LooksLikeWupFolder(path) || LooksLikeLoadiineFolder(path))
                {
                    yield return path;
                    continue;
                }

                var opts = new EnumerationOptions
                {
                    IgnoreInaccessible = true,
                    RecurseSubdirectories = true,
                    AttributesToSkip = FileAttributes.System | FileAttributes.Hidden
                };

                foreach (var f in Directory.EnumerateFiles(path, "*.*", opts))
                    if (SupportedExtensions.Contains(Path.GetExtension(f)))
                        yield return f;
            }
            else if (File.Exists(path) && SupportedExtensions.Contains(Path.GetExtension(path)))
            {
                yield return path;
            }
        }
    }

    private static bool LooksLikeLoadiineFolder(string path) =>
        Directory.Exists(Path.Combine(path, "code")) && Directory.Exists(Path.Combine(path, "content")) && Directory.Exists(Path.Combine(path, "meta"));

    private void AppendLog(string msg, LogLevel level = LogLevel.Info) => Application.Current.Dispatcher.Invoke(() => LogEntries.Add(new LogEntry { Message = msg, Level = level }));

    private void ClearLog() => Application.Current.Dispatcher.Invoke(() => LogEntries.Clear());
}
