using Avalonia;

namespace OurCut.App;

internal static class Program
{
    // Don't use Avalonia or anything that needs a SynchronizationContext before AppMain runs.
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    // Also used by the IDE previewer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithOurCutFonts()
            .LogToTrace();
}
