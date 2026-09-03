using NSW.WPF.UI;
using RomForge.Core;
using RomForge.Core.UI.Helpers;
using RomForge.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace RomForge.Views;

public partial class MainWindow : Window
{
    private static readonly ImageSource PatchCompletedOverlay = CreatePatchCompletedOverlay();
    private static readonly ImageSource PatchCompletedWindowIcon = CreatePatchCompletedWindowIcon();

    private bool _hasPatchCompletionNotification;

    private MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        DataContext = ViewModel;
        InitializeComponent();
        Closing += MainWindow_Closing;
        Activated += MainWindow_Activated;
        PreviewMouseDown += MainWindow_PreviewMouseDown;
        ViewModel.PatchVM.PatchCompleted += PatchVM_PatchCompleted;

        RestoreWindowState();
    }

    private static ImageSource CreatePatchCompletedOverlay()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(245, 158, 11)),
            new Pen(Brushes.White, 1.2),
            new EllipseGeometry(new Point(8, 8), 7, 7)));
        drawing.Children.Add(new GeometryDrawing(
            null,
            new Pen(Brushes.White, 2),
            Geometry.Parse("M 4 8 L 7 11 L 12 5")));
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private static ImageSource CreatePatchCompletedWindowIcon()
    {
        var processPath = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 확인할 수 없습니다.");
        using var appIcon = System.Drawing.Icon.ExtractAssociatedIcon(processPath)
            ?? throw new InvalidOperationException("실행 파일 아이콘을 읽을 수 없습니다.");

        var appIconImage = Imaging.CreateBitmapSourceFromHIcon(
            appIcon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(16, 16));
        appIconImage.Freeze();

        var drawing = new DrawingGroup();
        drawing.Children.Add(new ImageDrawing(appIconImage, new Rect(0, 0, 16, 16)));
        drawing.Children.Add(new ImageDrawing(PatchCompletedOverlay, new Rect(8, 8, 8, 8)));
        drawing.Freeze();

        var image = new DrawingImage(drawing);
        image.Freeze();
        return image;
    }

    private void PatchVM_PatchCompleted(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
            ShowPatchCompletionNotification();
        else
            _ = Dispatcher.BeginInvoke(ShowPatchCompletionNotification);
    }

    private void ShowPatchCompletionNotification()
    {
        _hasPatchCompletionNotification = true;
        TaskbarItemInfo ??= new TaskbarItemInfo();
        TaskbarItemInfo.Overlay = PatchCompletedOverlay;
        TaskbarItemInfo.Description = "패치 완료";
        Icon = PatchCompletedWindowIcon;
    }

    private void MainWindow_Activated(object? sender, EventArgs e) => ClearPatchCompletionNotification();

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e) => ClearPatchCompletionNotification();

    private void ClearPatchCompletionNotification()
    {
        if (!_hasPatchCompletionNotification)
            return;

        _hasPatchCompletionNotification = false;
        TaskbarItemInfo!.Overlay = null;
        TaskbarItemInfo.Description = null;
        Icon = null;
    }

    private void RestoreWindowState()
    {
        var cfg = AppConfig.Instance.Window;

        Left = cfg.Left;
        Top = cfg.Top;
        Width = cfg.Width;
        Height = cfg.Height;

        if (cfg.IsMaximized)
            WindowState = WindowState.Maximized;
    }

    private void SaveWindowState()
    {
        var cfg = AppConfig.Instance.Window;

        cfg.IsMaximized = WindowState == WindowState.Maximized;

        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);

        cfg.Left = bounds.Left;
        cfg.Top = bounds.Top;
        cfg.Width = bounds.Width;
        cfg.Height = bounds.Height;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hWnd = new WindowInteropHelper(this).Handle;
        int value = 1;

        _ = Win32API.DwmSetWindowAttribute(hWnd, 20, ref value, sizeof(int));
    }

    private void GridSplitter_DragCompleted(object sender, DragCompletedEventArgs e) => MainViewModel.LogBoxHeight = LogRow.Height.Value;

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveWindowState();
        MainViewModel.SaveConfig();

        bool busy = ViewModel.IsAnyChildLocked();

        if (!busy)
            return;

        var result = MessageBoxHelper.ShowQuestion("작업이 진행 중입니다. 취소하고 종료할까요?");

        if (result)
            ViewModel.CancelAll();
        else
            e.Cancel = true;
    }
}
