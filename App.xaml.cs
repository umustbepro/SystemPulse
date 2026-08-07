using System.IO;
using System.Windows;
using System.Windows.Threading;
using SystemPulse.Services;
using MessageBox = System.Windows.MessageBox;

namespace SystemPulse;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        try
        {
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

            var restartedAfterUpdate = e.Args.Any(argument =>
                argument.Equals("--updated-minimized", StringComparison.OrdinalIgnoreCase));
            if (e.Args.Length >= 3 && e.Args[0].Equals("--cleanup-update", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(e.Args[2], out var updaterProcessId))
                await UpdateService.CleanupDownloadedUpdateAsync(e.Args[1], updaterProcessId);

            var window = new MainWindow(restartedAfterUpdate);
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            ReportCrash(exception, "startup");
            Shutdown(1);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ReportCrash(e.Exception, "application");
        e.Handled = false;
    }

    private static void ReportCrash(Exception exception, string context)
    {
        try
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SystemPulse",
                "CrashLogs");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"SystemPulse-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, $"SystemPulse {context} error{Environment.NewLine}{DateTime.Now:O}{Environment.NewLine}{exception}");
            MessageBox.Show(
                $"SystemPulse encountered an unexpected {context} error and saved a crash log.\n\n{exception.Message}\n\nLog: {path}",
                "SystemPulse error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Never replace the original failure with a crash-reporting failure.
        }
    }
}
