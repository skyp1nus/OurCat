using Avalonia;
using OurCut.App.Services;
using OurCut.Mcp;

namespace OurCut.App;

internal static class Program
{
    // Don't use Avalonia or anything that needs a SynchronizationContext before AppMain runs.
    [STAThread]
    public static int Main(string[] args)
    {
        // "OurCut mcp": the MCP server Claude starts over stdio. It has no window; it forwards Claude's
        // tool calls to the editor (starting it when needed). Nothing else may write to stdout here.
        if (args is ["mcp", ..])
            return McpBridge.RunStdioAsync(EditorLauncher.Launch).GetAwaiter().GetResult();
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the IDE previewer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithOurCutFonts()
            .LogToTrace();
}
