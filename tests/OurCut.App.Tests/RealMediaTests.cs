using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.App.Views;
using OurCut.Media;
using OurCut.Media.Caching;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.App.Tests;

/// <summary>
/// The whole path with ffmpeg: open a generated video, wait for thumbnails, waveform and keyframes,
/// mark clips and export them. Skipped when ffmpeg is not installed.
/// </summary>
public sealed class RealMediaTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ourcut-real").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Temp folder; the OS cleans it up.
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>6 s, 320×180, keyframe every second, two named audio streams.</summary>
    private async Task<string> SampleAsync()
    {
        Assert.SkipWhen(NativeTools.FindTool("ffmpeg") is null || NativeTools.FindTool("ffprobe") is null,
            "ffmpeg/ffprobe are not installed.");
        string path = Path.Combine(_dir, "sample.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-f", "lavfi", "-i", "sine=frequency=440",
            "-f", "lavfi", "-i", "sine=frequency=880", "-t", "6", "-map", "0:v", "-map", "1:a", "-map", "2:a",
            "-c:v", "libx264", "-preset", "veryfast", "-g", "30", "-keyint_min", "30", "-sc_threshold", "0", "-bf", "2",
            "-pix_fmt", "yuv420p", "-c:a", "aac", "-metadata:s:a:0", "handler_name=Mic", "-metadata:s:a:1", "handler_name=Music",
            "-y", path,
        ], null, Ct), Ct);
        return path;
    }

    private async Task<(EditorViewModel Editor, MainWindow Window)> OpenAsync(string video)
    {
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))),
            new RecentFilesStore(Path.Combine(_dir, "recent.json")));
        var window = new MainWindow { DataContext = editor, Width = 1440, Height = 900 };
        window.Show();
        await editor.OpenMediaAsync(video);
        Assert.True(editor.HasFile, editor.StatusMessage);
        var preview = Assert.IsType<MediaPreview>(editor.Media);
        await preview.Analysis.WaitAsync(TimeSpan.FromSeconds(60), Ct);
        // The editor hears about the finished analysis through the preview's (throttled) Changed event.
        await PumpUntil(() => !editor.StatusRight.Contains("analysing", StringComparison.Ordinal) && editor.Session.Keyframes.Count > 0);
        return (editor, window);
    }

    private static async Task PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 200 && !done(); i++)
        {
            await Task.Delay(25, Ct);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    /// <summary>Clips [1.5, 3.2] and [4, 5.5], made with the I and O keys' commands.</summary>
    private static void MarkTwoClips(EditorViewModel editor)
    {
        editor.SetTime(1.5);
        editor.MarkIn();
        editor.SetTime(3.2);
        editor.MarkOut();
        editor.Select(null);
        editor.SetTime(4);
        editor.MarkIn();
        editor.SetTime(5.5);
        editor.MarkOut();
        Assert.Equal([(1.5, 3.2), (4.0, 5.5)], editor.Clips.Select(c => (c.Start, c.End)));
    }

    [AvaloniaFact]
    public async Task Opening_a_video_fills_the_timeline_from_ffmpeg()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        var preview = (MediaPreview)editor.Media!;

        Assert.Equal("sample", editor.ProjectName);
        Assert.Equal("sample.mp4 · 320×180 · 30 fps", editor.MediaInfo);
        Assert.Equal(["Mic", "Music"], editor.AudioLanes.Select(l => l.Label));
        Assert.Equal(6, preview.Keyframes.Count);
        Assert.Equal(preview.Keyframes, editor.Session.Keyframes);
        Assert.Equal(3, preview.ThumbnailCount);
        Assert.True(preview.Waveform.IsComplete);
        Assert.True(preview.AudioPeak(0, 1, 2) > 0.5);
        Assert.Null(preview.AnalysisError);
        Assert.Equal(video, Assert.Single(editor.RecentFiles).Path);

        MarkTwoClips(editor);
        editor.ZoomLevel = 0.3;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "real-media.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_second_open_reads_the_analysis_from_the_cache()
    {
        string video = await SampleAsync();
        var (first, w1) = await OpenAsync(video);
        w1.Close();
        var (second, w2) = await OpenAsync(video);
        var a = (MediaPreview)first.Media!;
        var b = (MediaPreview)second.Media!;
        Assert.Equal(a.Keyframes, b.Keyframes);
        Assert.Equal(a.ThumbnailCount, b.ThumbnailCount);
        Assert.Equal(a.Waveform.Filled, b.Waveform.Filled);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Exporting_writes_the_kept_clips_without_muted_tracks()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        editor.AudioLanes[0].IsMuted = true;
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;

        var export = editor.Export;
        export.Open();
        Assert.Equal(_dir, export.OutputFolder);
        Assert.Equal("MP4", export.Container);
        Assert.Equal("Copy (AAC 44.1 kHz)", export.Audio.Label);
        export.OutputFolder = outDir;
        export.KeepAllTracks = false;
        await export.StartAsync();

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal("Export complete", export.Title);
        Assert.Equal(["1 · Clip 1", "2 · Clip 2", "Merge into sample-cut.mp4"], export.Rows.Select(r => r.Name));
        Assert.All(export.Rows, r => Assert.True(r.IsDone));
        string output = Path.Combine(outDir, "sample-cut.mp4");
        Assert.Equal([output], Directory.GetFiles(outDir));
        Assert.Equal("Exported sample-cut.mp4", editor.StatusMessage);

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(["Music"], result.Audio.Select(a => a.Label));
        // Lossless: 1.0–3.2 and 4.0–5.5 (the in-points move back to keyframes), plus a few frames.
        Assert.InRange(result.Duration, 3.65, 4.3);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reencoding_into_separate_files_shows_one_row_per_file()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Path.Combine(_dir, "separate");

        var export = editor.Export;
        export.Open();
        export.Mode = ExportMode.Encode;
        export.Merge = false;
        export.Container = "MKV";
        export.Video = OurCut.Media.Export.VideoEncoding.H264Fast;
        export.OutputFolder = outDir;
        await export.StartAsync();

        Assert.True(export.IsDone, export.ErrorText);
        Assert.Equal(["sample-1-clip-1.mkv", "sample-2-clip-2.mkv"], export.Rows.Select(r => r.Name));
        var first = await MediaProbe.ProbeAsync(Path.Combine(outDir, "sample-1-clip-1.mkv"), Ct);
        Assert.Equal(1.7, first.Duration, 0.1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_failed_export_says_why()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string blocker = Path.Combine(_dir, "not-a-folder");
        await File.WriteAllTextAsync(blocker, "", Ct);

        var export = editor.Export;
        export.Open();
        export.OutputFolder = Path.Combine(blocker, "out");
        await export.StartAsync();

        Assert.True(export.HasError);
        Assert.Equal("Export failed", export.Title);
        Assert.False(export.IsRunning);
        Assert.True(export.IsExporting);
        export.Close();
        Assert.False(export.IsDialogOpen);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cancelling_an_export_removes_its_files()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "cancel")).FullName;

        var export = editor.Export;
        export.Open();
        export.Mode = ExportMode.Encode;
        export.OutputFolder = outDir;
        var running = export.StartAsync();
        export.Close();
        await running;

        Assert.False(export.IsDialogOpen);
        Assert.Equal("Export cancelled.", editor.StatusMessage);
        Assert.Empty(Directory.GetFiles(outDir));
        window.Close();
    }
}
