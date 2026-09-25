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
using OurCut.Transcription;
using OurCut.Transcription.Models;

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

    private async Task<(EditorViewModel Editor, MainWindow Window)> OpenAsync(string video, Action<EditorViewModel>? prepare = null)
    {
        var editor = App.CreateEditor(null, new FfmpegMediaOpener(new MediaCache(Path.Combine(_dir, "cache"))),
            new RecentFilesStore(Path.Combine(_dir, "recent.json")));
        // No transcription unless a test asks for it (the default models folder may have real models).
        editor.Settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "no-models")).FullName;
        prepare?.Invoke(editor);
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

    private static async Task PumpUntil(Func<bool> done, int tries = 200)
    {
        for (int i = 0; i < tries && !done(); i++)
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
    public async Task Silences_and_scene_changes_appear_on_the_timeline_and_Claude_can_cut_the_pauses()
    {
        // Cuts at 3, 6 and 9 s with steady motion in between; a tone with pauses at 2–4 s and 7–8.5 s.
        _ = await SampleAsync();
        string video = Path.Combine(_dir, "talk.mp4");
        const string Graph =
            "testsrc2=size=320x180:rate=30:duration=3[v0];smptehdbars=size=320x180:rate=30:duration=3[v1];" +
            "mandelbrot=size=320x180:rate=30,trim=duration=3[v2];testsrc=size=320x180:rate=30:duration=3[v3];" +
            "[v0][v1][v2][v3]concat=n=4:v=1:a=0,format=yuv420p[v];" +
            "sine=frequency=440:duration=2[a0];aevalsrc=0:d=2[a1];sine=frequency=440:duration=3[a2];aevalsrc=0:d=1.5[a3];" +
            "sine=frequency=440:duration=3.5[a4];[a0][a1][a2][a3][a4]concat=n=5:v=0:a=1[a]";
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-filter_complex", Graph, "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "veryfast",
            "-g", "30", "-c:a", "aac", "-y", video,
        ], null, Ct), Ct);

        var (editor, window) = await OpenAsync(video);
        await PumpUntil(() => editor.HasSceneData && editor.HasSilenceData);

        Assert.Equal("Silence bands: 2 pauses of a second or more", editor.SilenceTip);
        Assert.Equal("Scene changes: 3", editor.ScenesTip);
        var silences = editor.Media!.Silences;
        Assert.Equal([2.0, 7.0], silences.Select(r => Math.Round(r.Start)));
        Assert.Equal([3.0, 6.0, 9.0], editor.Media.SceneChanges.Select(t => Math.Round(t, 1)));

        // Claude's view of the same analysis, and its cut: the whole video minus the pauses, as one edit.
        var tools = new OurCut.Mcp.EditorTools(new EditorMcpHost(editor));
        var scenes = await tools.FindSceneChanges(threshold: 20);
        Assert.Equal(3, scenes.Count);
        var cut = await tools.CutSilences(padding: 0.1);
        Assert.Equal(3, editor.Clips.Count);
        Assert.StartsWith("Removed 2 silences", cut.Result, StringComparison.Ordinal);
        Assert.True(editor.Clips.All(c => c.IsAiChanged));
        // 3.5 s of pauses, less 0.1 s kept at each of their four edges.
        Assert.InRange(editor.OutputDuration, editor.Duration - 3.1 - 0.2, editor.Duration - 3.1 + 0.2);

        editor.ZoomLevel = 0;
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        using var frame = window.CaptureRenderedFrame();
        frame!.Save(Path.Combine(Screenshots.Directory, "silences-scenes.png"), new PngBitmapEncoderOptions());
        window.Close();
    }

    /// <summary>Says "word1", "word2"… one per piece, and counts the pieces.</summary>
    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public static int Pieces;

        public IReadOnlyList<Word> Recognize(float[] samples, double offset)
        {
            int n = Interlocked.Increment(ref Pieces);
            return [new Word($"word{n}", offset + 0.5, offset + 1)];
        }

        public void Dispose()
        {
        }
    }

    /// <summary>A models folder where the model counts as installed (its files exist, empty).</summary>
    private string FakeModels(TranscriptionModel model)
    {
        string models = Path.Combine(_dir, "models");
        string dir = Directory.CreateDirectory(Path.Combine(models, model.Id)).FullName;
        foreach (string f in model.Files)
            File.WriteAllText(Path.Combine(dir, f), "");
        return models;
    }

    [AvaloniaFact]
    public async Task A_video_is_transcribed_after_the_analysis_and_the_transcript_is_cached()
    {
        string video = await SampleAsync();
        string models = FakeModels(ModelCatalog.Parakeet);
        FakeRecognizer.Pieces = 0;
        void Prepare(EditorViewModel e)
        {
            e.Settings.ModelsFolder = models;
            e.RecognizerFactory = _ => new FakeRecognizer();
        }

        var (editor, window) = await OpenAsync(video, Prepare);
        await PumpUntil(() => editor.Media!.TranscriptState == TranscriptState.Done);

        var transcript = editor.Media!.Transcript!;
        Assert.Equal("parakeet-tdt-0.6b-v3", transcript.Model);
        Assert.Equal(["word1"], transcript.Words.Select(w => w.Text));
        Assert.Equal(1, FakeRecognizer.Pieces);
        window.Close();

        // Opened again: read from the cache, not recognized again.
        var (again, w2) = await OpenAsync(video, Prepare);
        await PumpUntil(() => again.Media!.TranscriptState == TranscriptState.Done);
        Assert.Equal(["word1"], again.Media!.Transcript!.Words.Select(w => w.Text));
        Assert.Equal(1, FakeRecognizer.Pieces);
        w2.Close();
    }

    [AvaloniaFact]
    public async Task Without_a_model_there_is_no_transcription()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        Assert.Contains("No transcription model", editor.StartTranscription(), StringComparison.Ordinal);
        Assert.Equal(TranscriptState.None, editor.Media!.TranscriptState);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Parakeet_transcribes_a_real_video()
    {
        string? models = Environment.GetEnvironmentVariable("OURCUT_MODELS_DIR");
        Assert.SkipUnless(models is { Length: > 0 } && new ModelStore(models).IsInstalled(ModelCatalog.Parakeet),
            "Parakeet is not installed in OURCUT_MODELS_DIR.");
        await SampleAsync();
        string speech = Path.Combine(new ModelStore(models!).DirectoryOf(ModelCatalog.Parakeet), "test_wavs", "en.wav");
        string video = Path.Combine(_dir, "speech.mp4");
        await Task.Run(() => ToolProcess.RunAsync("ffmpeg",
        [
            "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=30", "-i", speech, "-af", "adelay=2000,apad=pad_dur=2",
            "-shortest", "-c:v", "libx264", "-preset", "veryfast", "-c:a", "aac", "-y", video,
        ], null, Ct), Ct);

        var (editor, window) = await OpenAsync(video, e => e.Settings.ModelsFolder = models!);
        await PumpUntil(() => editor.Media!.TranscriptState is TranscriptState.Done or TranscriptState.Failed, 1500);

        var transcript = editor.Media!.Transcript!;
        Assert.Contains("ask not what your country can do for you", transcript.Text, StringComparison.OrdinalIgnoreCase);
        // The speech starts 2 s in.
        Assert.Equal(2, transcript.Words[0].Start, 0.4);
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
    public async Task Claude_exports_in_the_dialog_and_never_overwrites()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Path.Combine(_dir, "by-claude");
        var tools = new OurCut.Mcp.EditorTools(new EditorMcpHost(editor));

        var first = await tools.Export(container: "mkv", folder: outDir);

        Assert.Equal("done", first.Status);
        Assert.Equal([Path.Combine(outDir, "sample-cut.mkv")], first.Files);
        Assert.Equal("Lossless copy · MKV · merged", first.Settings);
        Assert.True(File.Exists(first.Files[0]));
        // The user sees it in the Export dialog, as if they had pressed Export.
        Assert.True(editor.Export.IsExporting && editor.Export.IsDone);

        var second = await tools.Export(container: "mkv", folder: outDir);
        Assert.Equal([Path.Combine(outDir, "sample-cut (2).mkv")], second.Files);
        Assert.Equal("done", (await tools.GetExportStatus()).Status);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Claude_can_cancel_its_export()
    {
        string video = await SampleAsync();
        var (editor, window) = await OpenAsync(video);
        MarkTwoClips(editor);
        string outDir = Directory.CreateDirectory(Path.Combine(_dir, "cancelled")).FullName;
        var host = new EditorMcpHost(editor);

        Assert.Null(host.StartExport(new OurCut.Mcp.ExportRequest(Mode: "reencode", Folder: outDir)));
        Assert.Equal("running", host.Export!.Status);
        Assert.Contains("already running", host.StartExport(new OurCut.Mcp.ExportRequest()), StringComparison.Ordinal);
        Assert.True(host.CancelExport());

        Assert.Equal("cancelled", host.Export!.Status);
        Assert.False(editor.Export.IsDialogOpen);
        await PumpUntil(() => editor.StatusMessage == "Export cancelled.");
        Assert.Empty(Directory.GetFiles(outDir));
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
