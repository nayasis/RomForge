using Common.WPF;
using Microsoft.Win32;
using NSW.WPF.Services;
using RomForge.Core.Models.Compression;
using RomForge.ViewModels;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace RomForge.Controls;

public partial class CompressTab : UserControl
{
    private readonly ListViewColumnSorter _sorter = new();
    private GridViewColumnHeader? _resizingHeader;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    public CompressTab()
    {
        InitializeComponent();

        DataContextChanged += CompressTab_DataContextChanged;
    }

    private void CompressTab_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (ViewModel?.CompressVM != null)
        {
            ViewModel.CompressVM.ScrollToItemRequested -= OnScrollToItemRequested;
            ViewModel.CompressVM.ScrollToItemRequested += OnScrollToItemRequested;
        }
    }

    private void OnScrollToItemRequested(object item)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (item != null)
                lvFiles.ScrollIntoView(item);
        }, DispatcherPriority.Background);
    }

    private void LvFiles_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void LvFiles_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            ViewModel.CompressVM.AddPaths(paths);
    }

    private void LvFiles_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
            return;

        var selected = lvFiles.SelectedItems.Cast<CompressFileItem>().ToList();

        ViewModel.CompressVM.RemoveItems(selected);
    }

    private void LvFiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var header = FindColumnHeader(e.OriginalSource as DependencyObject);

        if (header?.Column is null || e.GetPosition(header).X < header.ActualWidth - 10)
            return;

        _resizingHeader = header;
        _resizeStartX = e.GetPosition(lvFiles).X;
        _resizeStartWidth = header.Column.ActualWidth;
        Mouse.Capture(lvFiles);
        e.Handled = true;
    }

    private void LvFiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_resizingHeader?.Column is not null)
        {
            _resizingHeader.Column.Width = Math.Max(40, _resizeStartWidth + e.GetPosition(lvFiles).X - _resizeStartX);
            e.Handled = true;
            return;
        }

        var header = FindColumnHeader(e.OriginalSource as DependencyObject);
        Cursor = header?.Column is not null && e.GetPosition(header).X >= header.ActualWidth - 10
            ? Cursors.SizeWE
            : Cursors.Arrow;
    }

    private void LvFiles_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizingHeader is null)
            return;

        _resizingHeader = null;
        Mouse.Capture(null);
        e.Handled = true;
    }

    private static GridViewColumnHeader? FindColumnHeader(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is GridViewColumnHeader header)
                return header;

            element = VisualTreeHelper.GetParent(element);
        }

        return null;
    }

    private void ExtensionLabel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is FrameworkElement { DataContext: CompressFileItem item })
            ViewModel.CompressVM.ToggleOutputExtension(item);
    }

    private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = CompressMainViewModel.GetFileDialogFilter()
        };

        if (dlg.ShowDialog() == true)
            ViewModel.CompressVM.AddPaths(dlg.FileNames);
    }

    private void BtnAddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
        {
            Description = "추가할 폴더를 선택하세요",
            UseDescriptionForTitle = true
        };

        if (dlg.ShowDialog() == true)
            ViewModel.CompressVM.AddPaths([dlg.SelectedPath]);
    }

    private void BtnRemove_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<CompressFileItem>().ToList();

        ViewModel.CompressVM.RemoveItems(selected);
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e) => ViewModel.CompressVM.ClearItems();

    private async void BtnSettings_Click(object sender, RoutedEventArgs e) => await ViewModel.NavigateCompressSettings();

    private void LvFiles_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (lvFiles.SelectedItems.Count == 0)
            e.Handled = true;
    }

    private void MenuItem_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = lvFiles.SelectedItems.Cast<CompressFileItem>().ToList();

        if (selected.Count == 0)
            return;

        string? dir = Path.GetDirectoryName(selected[0].FilePath);

        dir?.OpenFolder();
    }

    private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e) => _sorter.HandleHeaderClick(e, lvFiles);
}
