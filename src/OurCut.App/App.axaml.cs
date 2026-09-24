using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OurCut.App.Demo;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.Media;

namespace OurCut.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var editor = CreateEditor(ParseDemoScreen(desktop.Args ?? []));
            desktop.MainWindow = new MainWindow { DataContext = editor };
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the editor. With a demo screen it shows the design's sample project in that state;
    /// otherwise it starts empty.
    /// </summary>
    public static EditorViewModel CreateEditor(DesignScreen? demo)
    {
        var editor = new EditorViewModel();
        // Media opening arrives with the FFmpeg/libmpv milestones; until then Open shows the sample project.
        editor.OpenRequested += (_, _) => DemoScenario.Apply(editor, DesignScreen.Editing);
        if (demo is { } screen)
            DemoScenario.Apply(editor, screen);
        else
            _ = CheckFfmpegAsync(editor);
        return editor;
    }

    private static async Task CheckFfmpegAsync(EditorViewModel editor)
    {
        string? version = await Task.Run(() => NativeTools.GetVersionAsync("ffmpeg")).ConfigureAwait(true);
        editor.ToolStatus = version is null
            ? "ffmpeg not found · run scripts/fetch-deps.ps1"
            : $"ffmpeg {version} · ready";
    }

    /// <summary>Reads <c>--demo &lt;empty|editing|ai|export|exporting&gt;</c> from the command line.</summary>
    public static DesignScreen? ParseDemoScreen(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] != "--demo")
                continue;
            string value = i + 1 < args.Count ? args[i + 1] : "editing";
            return Enum.TryParse<DesignScreen>(value, ignoreCase: true, out var screen) ? screen : DesignScreen.Editing;
        }
        return null;
    }
}
