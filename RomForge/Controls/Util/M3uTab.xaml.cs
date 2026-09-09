using Microsoft.Win32;
using RomForge.Core.Models.Util;
using RomForge.ViewModels.Util;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RomForge.Controls.Util;

public partial class M3uTab : UserControl
{
    private Point _dragStartPoint;
    private M3uFileItem? _dragItem;

    private M3uMainViewModel ViewModel => (M3uMainViewModel)DataContext;

    public M3uTab() => InitializeComponent();

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
            e.Effects = DragDropEffects.Copy;
        else if (e.Data.GetDataPresent(typeof(M3uFileItem))
            && e.Data.GetData(typeof(M3uFileItem)) is M3uFileItem source
            && GetItemAt(e.OriginalSource) is M3uFileItem target
            && string.Equals(source.GroupPath, target.GroupPath, StringComparison.OrdinalIgnoreCase))
            e.Effects = DragDropEffects.Move;
        else
            e.Effects = DragDropEffects.None;

        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(M3uFileItem))
            && e.Data.GetData(typeof(M3uFileItem)) is M3uFileItem source
            && GetItemAt(e.OriginalSource) is M3uFileItem target)
        {
            var targetContainer = FindParent<ListViewItem>(e.OriginalSource as DependencyObject);
            bool insertAfter = targetContainer is not null
                && e.GetPosition(targetContainer).Y >= targetContainer.ActualHeight / 2;

            ViewModel.MoveItem(source, target, insertAfter);
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await ViewModel.AddPaths(paths);

        e.Handled = true;
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(fileList);
        _dragItem = GetItemAt(e.OriginalSource);
    }

    private void FileList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var currentPoint = e.GetPosition(fileList);

        if (Math.Abs(currentPoint.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(currentPoint.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var dragItem = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(fileList, new DataObject(typeof(M3uFileItem), dragItem), DragDropEffects.Move);
    }

    private void FileList_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
            ViewModel.RemoveItems(fileList.SelectedItems.Cast<M3uFileItem>());
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Multiselect = true, Filter = "파일|*.*" };
        if (dialog.ShowDialog() == true)
            await ViewModel.AddPaths(dialog.FileNames);
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "M3U 파일을 만들 폴더 선택" };
        if (dialog.ShowDialog() == true)
            await ViewModel.AddPaths([dialog.FolderName]);
    }

    private void Remove_Click(object sender, RoutedEventArgs e) => ViewModel.RemoveItems(fileList.SelectedItems.Cast<M3uFileItem>());

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel.ClearItems();

    private static M3uFileItem? GetItemAt(object originalSource) =>
        FindParent<ListViewItem>(originalSource as DependencyObject)?.DataContext as M3uFileItem;

    private static T? FindParent<T>(DependencyObject? source) where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T target)
                return target;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
