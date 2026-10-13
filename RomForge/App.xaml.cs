using Application = System.Windows.Application;

namespace RomForge;

public partial class App : Application
{
    protected override async void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 0)
        {
            StartupUri = new Uri("Views/MainWindow.xaml", UriKind.Relative);
            return;
        }

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        Cli.CliConsole.Initialize();
        Shutdown(await Cli.CliRunner.RunAsync(e.Args));
    }
}
