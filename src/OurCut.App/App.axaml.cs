using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.Core.Model;
using OurCut.Core.Serialization;
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
            var window = new MainWindow { DataContext = editor };
            editor.Dialogs = new StorageFileDialogs(window);
            desktop.MainWindow = window;
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
        // Opening real media arrives with the FFmpeg/libmpv milestones; until then Open shows the sample project.
        editor.OpenRequested += (_, _) => DemoScenario.OpenSample(editor);
        editor.OpenProjectRequested += async (_, path) => await OpenProjectAsync(editor, path).ConfigureAwait(true);
        if (demo is { } screen)
            DemoScenario.Apply(editor, screen);
        else
            _ = CheckFfmpegAsync(editor);
        return editor;
    }

    public static async Task OpenProjectAsync(EditorViewModel editor, string path)
    {
        Project project;
        try
        {
            project = await ProjectFile.LoadAsync(path).ConfigureAwait(true);
        }
        catch (Exception e) when (e is ProjectFileException or IOException or UnauthorizedAccessException)
        {
            editor.ShowMessage("Could not open the project: " + e.Message);
            return;
        }
        if (project.Source is null)
        {
            editor.ShowMessage("The project has no source video.");
            return;
        }
        editor.LeaveDemo();
        // Until media probing is connected the sample file stands in for the project's video.
        editor.LoadProject(project, new DesignSample(), Path.GetFileName(project.Source.Path), path);
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
