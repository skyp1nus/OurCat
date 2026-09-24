using Avalonia;
using Avalonia.Headless;
using OurCut.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OurCut.App.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering (not headless drawing) so tests can capture screenshots.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithOurCutFonts();
}
