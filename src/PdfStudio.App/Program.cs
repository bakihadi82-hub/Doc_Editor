using Avalonia;
using PdfStudio.App.Services;
using PdfStudio.Editing.PdfSharp;

namespace PdfStudio.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!SingleInstanceCoordinator.TryAcquirePrimaryInstance(
                App.HandleExternalLaunchMessage,
                out var singleInstance))
        {
            SingleInstanceCoordinator.SendArgumentsToPrimaryInstanceAsync(args)
                .GetAwaiter()
                .GetResult();
            return;
        }

        using var _ = singleInstance;

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            CrashLogger.LogException(e.ExceptionObject as Exception, "AppDomain.UnhandledException");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashLogger.LogException(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        try
        {
            EncodingBootstrap.EnsureRegistered();
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            CrashLogger.LogException(ex, "Program.Main");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

