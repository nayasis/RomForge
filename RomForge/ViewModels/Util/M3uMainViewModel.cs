using Common;
using Common.WPF.ViewModels;
using RomForge.Core.Models;
using RomForge.Core.Models.Util;
using RomForge.Core.Services;
using RomForge.Core.UI.Command;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;

namespace RomForge.ViewModels.Util;

public sealed class M3uMainViewModel : ToolTabViewModel
{
    private CancellationTokenSource _cts = new();
    private bool _hasCompleted;

    public ObservableCollection<LogEntry> LogEntries { get; } = [];
    public ObservableCollection<M3uFileItem> FileItems { get; } = [];

    public Visibility HintVisibility => FileItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public ICommand RunCommand { get; }

    public M3uMainViewModel()
    {
        RunCommand = new RelayCommand(async _ => await RunAsync(), _ => !IsLocked && FileItems.Count > 0);
        CancelCommand = new RelayCommand(_ => _cts.Cancel(), _ => IsLocked);
    }

    public Task AddPaths(IEnumerable<string> paths)
    {
        if (_hasCompleted)
        {
            FileItems.Clear();
            LogEntries.Clear();
            _hasCompleted = false;
        }

        var existingPaths = FileItems.Select(item => item.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            IEnumerable<string> files = Directory.Exists(path)
                ? Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                : File.Exists(path) ? [path] : [];

            foreach (var file in files)
            {
                if (Path.GetExtension(file).Equals(".m3u", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (existingPaths.Add(file))
                    FileItems.Add(new M3uFileItem(file));
            }
        }

        SortItems();

        OnPropertyChanged(nameof(HintVisibility));
        CommandManager.InvalidateRequerySuggested();
        return Task.CompletedTask;
    }

    public void RemoveItems(IEnumerable<M3uFileItem> items)
    {
        foreach (var item in items.ToList())
            FileItems.Remove(item);

        OnPropertyChanged(nameof(HintVisibility));
        CommandManager.InvalidateRequerySuggested();
    }

    public void ClearItems()
    {
        FileItems.Clear();
        _hasCompleted = false;
        OnPropertyChanged(nameof(HintVisibility));
        CommandManager.InvalidateRequerySuggested();
    }

    public bool MoveItem(M3uFileItem source, M3uFileItem target, bool insertAfter)
    {
        if (ReferenceEquals(source, target)
            || !string.Equals(source.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase))
            return false;

        int sourceIndex = FileItems.IndexOf(source);
        int targetIndex = FileItems.IndexOf(target);

        if (sourceIndex < 0 || targetIndex < 0)
            return false;

        int destinationIndex = targetIndex + (insertAfter ? 1 : 0);

        if (sourceIndex < destinationIndex)
            destinationIndex--;

        if (sourceIndex == destinationIndex)
            return false;

        FileItems.Move(sourceIndex, destinationIndex);
        return true;
    }

    private async Task RunAsync()
    {
        _cts.Dispose();
        _cts = new CancellationTokenSource();

        LogEntries.Clear();

        using (BeginWork())
        {
            try
            {
                var groups = FileItems
                    .GroupBy(item => Path.GetDirectoryName(item.FilePath)!, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => Path.GetFileName(group.Key), StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var group in groups)
                {
                    _cts.Token.ThrowIfCancellationRequested();

                    var result = await Task.Run(
                        () => M3uFileCreator.CreateOrOverwrite(group.Key, group.Select(item => item.FilePath)),
                        _cts.Token);

                    AppendLog(result.Created ? $"M3U 생성 완료: {result.Path}" : $"기존 M3U 덮어쓰기 완료: {result.Path}", LogLevel.Ok);
                }

                _hasCompleted = true;
                AppendLog($"M3U 생성 작업 완료: {groups.Count}개 그룹", LogLevel.Highlight);
            }
            catch (OperationCanceledException)
            {
                AppendLog("M3U 생성 작업이 취소되었습니다.", LogLevel.Error);
            }
            catch (Exception ex)
            {
                AppendLog($"M3U 생성 실패: {ex.Message}", LogLevel.Error);
            }
        }
    }

    private void AppendLog(string message, LogLevel level)
    {
        Application.Current?.Dispatcher?.Invoke(() => LogEntries.Add(new LogEntry { Message = message, Level = level }));
    }

    private void SortItems()
    {
        var sortedItems = FileItems
            .OrderBy(item => item.GroupPath, StringComparer.Ordinal)
            .ThenBy(item => item.FileName, StringComparer.Ordinal)
            .ToList();

        if (FileItems.SequenceEqual(sortedItems))
            return;

        FileItems.Clear();

        foreach (var item in sortedItems)
            FileItems.Add(item);
    }
}
