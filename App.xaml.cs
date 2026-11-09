using System.Threading.Tasks;
using System.Windows;
using VideoEditor.Services;

namespace VideoEditor;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            FastDiagnosticsService.Log("WPF dispatcher", e.Exception);
            MessageBox.Show(
                $"MyEditor caught an unexpected problem. {FastDiagnosticsService.Explain(e.Exception)}\n\nDetails: {FastDiagnosticsService.LogFilePath}",
                "MyEditor · Quick recovery", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = e.Exception is not OutOfMemoryException;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception) FastDiagnosticsService.Log("AppDomain", exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FastDiagnosticsService.Log("Background task", e.Exception);
            e.SetObserved();
        };
    }
}
