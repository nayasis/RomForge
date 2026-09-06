using Microsoft.Win32;
using RomForge.Core.Models.Util;
using RomForge.ViewModels.Util;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace RomForge.Controls.Util;

public partial class M3uTab : UserControl
{
    private M3uMainViewModel ViewModel => (M3uMainViewModel)DataContext;

    public M3uTab() => InitializeComponent();

    private void FileList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void FileList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await ViewModel.AddPaths(paths);
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
}
