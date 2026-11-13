using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using PdfStudio.App.Services;
using PdfStudio.App.ViewModels;
using PdfStudio.App.Views;
using PdfStudio.Core.Editing;
using PdfStudio.Core.Rendering;
using PdfStudio.Editing.PdfSharp;
using PdfStudio.Rendering.Pdfium;

namespace PdfStudio.App;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPdfRenderEngine, PdfiumRenderEngine>();
        services.AddSingleton<IPdfEditorService, PdfSharpEditorService>();
        services.AddSingleton<MainWindowViewModel>();
        Services = services.BuildServiceProvider();

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            CrashLogger.LogException(e.Exception, "Dispatcher.UIThread.UnhandledException");
            if (Services?.GetService<MainWindowViewModel>() is { } vm)
            {
                vm.ShowErrorDialog($"An unexpected error occurred:\n{e.Exception.Message}");
                e.Handled = true;
            }
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow
            {
                DataContext = vm
            };

            desktop.MainWindow = window;
            desktop.Exit += async (_, _) =>
            {
                if (Services is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
            };

            if (desktop.Args is { Length: > 0 } && !string.IsNullOrWhiteSpace(desktop.Args[0]))
            {
                string startupPath = Path.GetFullPath(desktop.Args[0]);
                if (File.Exists(startupPath))
                {
                    _ = vm.LoadFileAsync(startupPath);
                }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    public static void HandleExternalLaunchMessage(string message)
    {
        Dispatcher.UIThread.Post(async () =>
        {
            if (Current is not App app) return;

            if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            {
                if (window.WindowState == WindowState.Minimized)
                {
                    window.WindowState = WindowState.Normal;
                }
                window.Activate();
                window.Topmost = true;
                window.Topmost = false;
            }

            if (!string.Equals(message, SingleInstanceCoordinator.ActivateCommand, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(message))
            {
                string fullPath = Path.GetFullPath(message);
                if (File.Exists(fullPath) &&
                    app.Services?.GetService<MainWindowViewModel>() is { } vm)
                {
                    await vm.LoadFileAsync(fullPath);
                }
            }
        });
    }
}

