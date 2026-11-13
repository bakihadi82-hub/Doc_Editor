using Avalonia;
using Avalonia.Headless;
using PdfStudio.App;

[assembly: AvaloniaTestApplication(typeof(PdfStudio.Tests.TestAppBuilder))]

namespace PdfStudio.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<PdfStudio.App.App>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .WithInterFont();
}

