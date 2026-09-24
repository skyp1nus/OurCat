using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.Media;
using OurCut.Media.Caching;

namespace OurCut.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? [];
            var demo = ParseDemoScreen(args);
            var editor = CreateEditor(demo, new FfmpegMediaOpener(new MediaCache()),
                demo is null ? new RecentFilesStore(RecentFilesStore.DefaultFile) : null);
            if (demo is null)
            {
                var settings = new AppSettingsStore(AppSettingsStore.DefaultFile);
                editor.Settings.Load(settings.Load());
                editor.Settings.Store = settings;
            }
            var window = new MainWindow { DataContext = editor };
            editor.Dialogs = new StorageFileDialogs(window);
            desktop.MainWindow = window;
            if (ParseFileArgument(args) is { } file)
                _ = editor.OpenPath(file);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Creates the editor. With a demo screen it shows the design's sample project in that state;
    /// otherwise it starts empty.
    /// </summary>
    /// <param name="opener">Opens videos; null when the editor never opens real files (tests).</param>
    /// <param name="recent">Recent files list; null keeps no history.</param>
    public static EditorViewModel CreateEditor(DesignScreen? demo, IMediaOpener? opener = null, RecentFilesStore? recent = null)
    {
        var editor = new EditorViewModel { MediaOpener = opener, RecentStore = recent };
        if (demo is { } screen)
        {
            DemoScenario.Apply(editor, screen);
        }
        else
        {
            editor.LoadRecentFiles();
            _ = CheckFfmpegAsync(editor);
        }
        return editor;
    }

    private static async Task CheckFfmpegAsync(EditorViewModel editor)
    {
        string? version = await Task.Run(() => NativeTools.GetVersionAsync("ffmpeg")).ConfigureAwait(true);
        editor.ToolStatus = version is null
            ? "ffmpeg not found · run scripts/fetch-deps.ps1"
            : $"ffmpeg {version} · ready";
    }

    /// <summary>Reads <c>--demo &lt;empty|editing|ai|export|exporting|settings&gt;</c> from the command line.</summary>
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

    /// <summary>A video or project passed on the command line ("Open with OurCut").</summary>
    public static string? ParseFileArgument(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--demo")
            {
                i++;
                continue;
            }
            if (!args[i].StartsWith("--", StringComparison.Ordinal) && File.Exists(args[i]))
                return args[i];
        }
        return null;
    }
}
