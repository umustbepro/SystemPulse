using System.Windows;
using SystemPulse.Services;
using MessageBox = System.Windows.MessageBox;

namespace SystemPulse;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Length == 3 && e.Args[0].Equals("--apply-update", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(e.Args[2], out var processId))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var result = await UpdateService.ApplyDownloadedUpdateAsync(e.Args[1], processId);
            if (!result.Success)
                MessageBox.Show(result.Message, "SystemPulse update", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(result.Success ? 0 : 1);
            return;
        }

        if (e.Args.Length == 3 && e.Args[0].Equals("--cleanup-update", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(e.Args[2], out var updaterProcessId))
            await UpdateService.CleanupDownloadedUpdateAsync(e.Args[1], updaterProcessId);

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }
}
