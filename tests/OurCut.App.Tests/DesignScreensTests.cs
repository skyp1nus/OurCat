using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using OurCut.App.Controls;
using OurCut.App.Demo;
using OurCut.App.Views;

namespace OurCut.App.Tests;

/// <summary>
/// Renders each screen of the design with its sample data at the design size (1440×900) and
/// saves a screenshot to artifacts/screenshots/ for comparison with design/project/OurCut.dc.html.
/// </summary>
public class DesignScreensTests
{
    [AvaloniaTheory]
    [InlineData(DesignScreen.Empty)]
    [InlineData(DesignScreen.Editing)]
    [InlineData(DesignScreen.Ai)]
    [InlineData(DesignScreen.Export)]
    [InlineData(DesignScreen.Exporting)]
    [InlineData(DesignScreen.Settings)]
    public void Design_screen_renders_at_design_size(DesignScreen screen)
    {
        var editor = App.CreateEditor(screen);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        using var frame = window.CaptureRenderedFrame();

        Assert.NotNull(frame);
        Assert.Equal(1440, frame!.PixelSize.Width);
        Assert.Equal(900, frame.PixelSize.Height);
        frame.Save(Path.Combine(Screenshots.Directory, $"{screen.ToString().ToLowerInvariant()}.png"), new PngBitmapEncoderOptions());
        window.Close();
    }
}

public class TimelineZoomTests
{
    [AvaloniaFact]
    public void Zooming_in_keeps_the_playhead_in_view_and_enables_scrolling()
    {
        var editor = App.CreateEditor(DesignScreen.Editing);
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var timeline = window.GetVisualDescendants().OfType<TimelineControl>().Single();

        editor.ZoomLevel = 0.5;
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();

        Assert.True(timeline.MaxScroll > 0);
        double playheadX = editor.Time / editor.Duration * timeline.ExtentWidth - timeline.ScrollOffset;
        Assert.InRange(playheadX, 0, timeline.Bounds.Width);
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "editing-zoomed.png"), new PngBitmapEncoderOptions());
        window.Close();
    }
}

internal static class Screenshots
{
    public static string Directory { get; } = Create();

    private static string Create()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OurCut.slnx")))
            dir = dir.Parent;
        string root = dir?.FullName ?? AppContext.BaseDirectory;
        string path = Path.Combine(root, "artifacts", "screenshots");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }
}
