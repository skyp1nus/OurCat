using System.Text.Json;
using OurCut.Core.Model;
using OurCut.Media.Caching;
using OurCut.Media.Export;
using OurCut.Media.Previews;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.Media.Tests.Integration;

/// <summary>Runs ffprobe/ffmpeg on generated media. Skipped when the tools are not installed.</summary>
public class MediaIntegrationTests(SampleMediaFixture media) : IClassFixture<SampleMediaFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Probe_reads_streams_and_titles()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);

        Assert.Equal(ContainerFamily.Mov, info.Family);
        Assert.Equal(10, info.Duration, 1);
        var v = Assert.IsType<VideoStreamInfo>(info.Video);
        Assert.Equal(("h264", 320, 180, 30.0), (v.Codec, v.Width, v.Height, v.FrameRate));
        Assert.True(v.HasBFrames);
        Assert.Equal(["Mic", "Music"], info.Audio.Select(a => a.Label));
        Assert.Equal([0, 1], info.Audio.Select(a => a.Position));
        Assert.Equal("sample clip.mp4 · 320×180 · 30 fps", info.Summary);
    }

    [Fact]
    public async Task Probe_recognises_matroska()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mkv, Ct);
        Assert.Equal(ContainerFamily.Matroska, info.Family);
        Assert.Equal(["Mic", "Music"], info.Audio.Select(a => a.Label));
    }

    [Fact]
    public async Task Probe_of_a_non_media_file_fails_with_ffprobes_message()
    {
        media.SkipIfUnavailable();
        string path = Path.Combine(media.Folder, "notes.txt");
        await File.WriteAllTextAsync(path, "not a video", Ct);
        var ex = await Assert.ThrowsAsync<MediaToolException>(() => MediaProbe.ProbeAsync(path, Ct));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task Keyframes_are_found_every_second(string kind)
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(kind == "mp4" ? media.Mp4 : media.Mkv, Ct);
        var reports = new List<double>();
        double[] keyframes = await KeyframeScanner.ScanAsync(info, new SyncProgress<double>(reports.Add), Ct);

        Assert.Equal(10, keyframes.Length);
        Assert.InRange(keyframes[0], 0, 0.05);
        for (int i = 0; i < keyframes.Length; i++)
            Assert.Equal(i, keyframes[i] - keyframes[0], 2);
        Assert.Equal(1, reports[^1]);
    }

    [Fact]
    public async Task Waveform_has_the_level_of_each_stream()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var data = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, data, cancellationToken: Ct);

        Assert.True(data.IsComplete);
        Assert.InRange(data.Filled, 990, data.Capacity);
        // lavfi's sine is at 1/8 amplitude (-18 dB).
        Assert.Equal(WaveformData.ToDisplay(0.125), data.Peak(0, 2, 3), 1);
        Assert.Equal(WaveformData.ToDisplay(0.125), data.Peak(1, 2, 3), 1);
    }

    [Fact]
    public async Task Thumbnails_are_decoded_at_the_requested_interval()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var frames = new List<ThumbnailFrame>();
        await ThumbnailExtractor.ExtractAsync(info, frames.Add, 90, 2, Ct);

        Assert.Equal(5, frames.Count);
        Assert.Equal([0.0, 2, 4, 6, 8], frames.Select(f => f.Time));
        Assert.All(frames, f => Assert.Equal((160, 90, 160 * 90 * 4), (f.Width, f.Height, f.Bgra.Length)));
        Assert.Contains(frames[0].Bgra, b => b > 32);
    }

    [Fact]
    public async Task Cache_round_trips_analysis_results()
    {
        media.SkipIfUnavailable();
        var info = await MediaProbe.ProbeAsync(media.Mp4, Ct);
        var cache = new MediaCache(Path.Combine(media.Folder, "cache"));
        Assert.Null(cache.LoadKeyframes(info.Path));
        Assert.Null(cache.LoadWaveform(info.Path));
        Assert.Null(cache.LoadThumbnails(info.Path, 90));

        double[] keyframes = await KeyframeScanner.ScanAsync(info, cancellationToken: Ct);
        var waveform = WaveformExtractor.Create(info);
        await WaveformExtractor.ExtractAsync(info, waveform, cancellationToken: Ct);
        var frames = new List<ThumbnailFrame>();
        await ThumbnailExtractor.ExtractAsync(info, frames.Add, 90, 2, Ct);

        cache.SaveKeyframes(info.Path, keyframes);
        cache.SaveWaveform(info.Path, waveform);
        cache.SaveThumbnails(info.Path, frames);

        Assert.Equal(keyframes, cache.LoadKeyframes(info.Path));
        var loadedWave = Assert.IsType<WaveformData>(cache.LoadWaveform(info.Path));
        Assert.Equal(waveform.Filled, loadedWave.Filled);
        Assert.Equal(waveform.Peak(1, 0, 10), loadedWave.Peak(1, 0, 10));
        var loadedFrames = Assert.IsType<IReadOnlyList<ThumbnailFrame>>(cache.LoadThumbnails(info.Path, 90), exactMatch: false);
        Assert.Equal(frames.Select(f => f.Time), loadedFrames.Select(f => f.Time));
        for (int i = 0; i < frames.Count; i++)
            Assert.InRange(MeanDifference(frames[i].Bgra, loadedFrames[i].Bgra), 0, 8);
        Assert.Null(cache.LoadThumbnails(info.Path, 60));
    }

    [Fact]
    public async Task Cache_misses_after_the_file_changes()
    {
        media.SkipIfUnavailable();
        string copy = Path.Combine(media.Folder, "changing.mp4");
        File.Copy(media.Mp4, copy, overwrite: true);
        var cache = new MediaCache(Path.Combine(media.Folder, "cache2"));
        cache.SaveKeyframes(copy, [0, 1]);
        Assert.NotNull(cache.LoadKeyframes(copy));

        await File.AppendAllTextAsync(copy, "x", Ct);
        Assert.Null(cache.LoadKeyframes(copy));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public async Task Lossless_merge_keeps_every_stream_and_adds_chapters(string kind)
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(kind == "mp4" ? media.Mp4 : media.Mkv);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { Container = kind == "mp4" ? OutputContainer.Mp4 : OutputContainer.Mkv };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        var reports = new List<ExportProgress>();
        var written = await ExportRunner.RunAsync(plan, new SyncProgress<ExportProgress>(reports.Add), Ct);

        string output = Assert.Single(written);
        Assert.Equal($"sample-cut.{kind}", Path.GetFileName(output));
        Assert.Equal([output], Directory.GetFiles(folder));

        var result = await MediaProbe.ProbeAsync(output, Ct);
        AssertLosslessLength(plan.OutputDuration, result.Duration, plan.Clips.Count);
        Assert.Equal("h264", result.Video?.Codec);
        Assert.Equal(["Mic", "Music"], result.Audio.Select(a => a.Label));

        // Chapters follow the real length of each cut, so the second starts where its clip does.
        var chapters = await Chapters(output);
        Assert.Equal(["Intro", "Demo — import"], chapters.Select(c => c.Title));
        Assert.Equal(0, chapters[0].Start, 2);
        AssertLosslessLength(plan.Clips[0].OutputDuration, chapters[1].Start, 1);

        Assert.Equal(1, reports[^1].Overall, 9);
        Assert.True(reports.Select(r => r.Overall).Zip(reports.Skip(1).Select(r => r.Overall)).All(p => p.Second >= p.First - 1e-9),
            "Overall progress must not go backwards.");
    }

    [Fact]
    public async Task Separate_files_have_one_clip_each()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder) with { Merge = false });

        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        Assert.Equal(["sample-1-intro.mp4", "sample-2-demo-import.mp4"], written.Select(Path.GetFileName));
        AssertLosslessLength(2.2, (await MediaProbe.ProbeAsync(written[0], Ct)).Duration, 1);
        AssertLosslessLength(2.0, (await MediaProbe.ProbeAsync(written[1], Ct)).Duration, 1);
        Assert.Equal(2, Directory.GetFiles(folder).Length);
    }

    [Fact]
    public async Task Reencoded_merge_is_frame_accurate()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { Mode = CutMode.Reencode, Video = VideoEncoding.H264Fast };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        var result = await MediaProbe.ProbeAsync(output, Ct);
        Assert.Equal(3.7, result.Duration, 0.1);
        Assert.Equal(["aac", "aac"], result.Audio.Select(a => a.Codec));
        Assert.Equal(2, (await Chapters(output)).Count);
        Assert.Equal([output], Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Reencoded_clips_with_copied_audio_have_no_lead_in()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with
        {
            Mode = CutMode.Reencode, Merge = false, Container = OutputContainer.Mkv, Video = VideoEncoding.H264Fast,
        };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        var written = await ExportRunner.RunAsync(plan, cancellationToken: Ct);

        var first = await MediaProbe.ProbeAsync(written[0], Ct);
        Assert.Equal(1.7, first.Duration, 0.05);
        Assert.Equal(["aac", "aac"], first.Audio.Select(a => a.Codec));
    }

    [Fact]
    public async Task Muted_tracks_are_left_out()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var settings = Settings(folder) with { KeepAllTracks = false, AudioStreamIndexes = [info.Audio[1].Index] };
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, settings);

        string output = Assert.Single(await ExportRunner.RunAsync(plan, cancellationToken: Ct));

        Assert.Equal(["Music"], (await MediaProbe.ProbeAsync(output, Ct)).Audio.Select(a => a.Label));
    }

    [Fact]
    public async Task Cancelling_leaves_no_files_behind()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        // Cancel when the second cut starts: the first temp file and the lists already exist.
        var progress = new SyncProgress<ExportProgress>(p =>
        {
            if (p.StepIndex == 1)
                cts.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(plan, progress, cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Cancelling_before_the_first_step_removes_the_chapters_file()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        // A re-encoded merge writes its chapters file before ffmpeg runs.
        var plan = ExportPlanner.Plan(Project(info), info, keyframes, Settings(folder) with { Mode = CutMode.Reencode });
        Assert.NotNull(plan.ChaptersPath);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportRunner.RunAsync(plan, cancellationToken: cts.Token));

        Assert.Empty(Directory.GetFiles(folder));
    }

    [Fact]
    public async Task Ffmpeg_errors_are_reported_and_cleaned_up()
    {
        media.SkipIfUnavailable();
        var (info, keyframes) = await Analyse(media.Mp4);
        string folder = media.NewOutputFolder();
        // A stream index that does not exist makes ffmpeg fail.
        var broken = info with { Audio = [info.Audio[0] with { Index = 9 }] };
        var plan = ExportPlanner.Plan(Project(info), broken, keyframes, Settings(folder));

        var ex = await Assert.ThrowsAsync<MediaToolException>(() => ExportRunner.RunAsync(plan, cancellationToken: Ct));

        // The explaining line, not ffmpeg's generic "Error opening output files" (its wording varies by version:
        // 6.x names the map, "Stream map '0:9' …"; 9.x leaves it out).
        Assert.Contains("matches no streams", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(folder));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static async Task<(MediaInfo Info, double[] Keyframes)> Analyse(string path)
    {
        var info = await MediaProbe.ProbeAsync(path, Ct);
        return (info, await KeyframeScanner.ScanAsync(info, cancellationToken: Ct));
    }

    /// <summary>
    /// A stream copy never loses content, but ffmpeg ends it by decode time, so with B-frames each clip
    /// comes out up to a few frames longer than planned.
    /// </summary>
    private static void AssertLosslessLength(double planned, double actual, int clips) =>
        Assert.InRange(actual, planned - 0.03, planned + 0.25 * clips);

    private static Project Project(MediaInfo info) => new("sample", info.ToSourceMedia(),
    [
        new Clip(1, "Intro", 1.5, 3.2),
        new Clip(2, "Demo — import", 5, 7),
        new Clip(3, "Skipped", 8, 9, IsIncluded: false),
    ]);

    private static ExportSettings Settings(string folder) => new() { OutputFolder = folder, BaseName = "sample" };

    private static async Task<List<(string Title, double Start)>> Chapters(string path)
    {
        string json = await ToolProcess.ReadAllTextAsync("ffprobe", ["-v", "error", "-show_chapters", "-of", "json", "-i", path], Ct);
        using var doc = JsonDocument.Parse(json);
        return [.. doc.RootElement.GetProperty("chapters").EnumerateArray().Select(c => (
            c.GetProperty("tags").GetProperty("title").GetString()!,
            double.Parse(c.GetProperty("start_time").GetString()!, CultureInfo.InvariantCulture)))];
    }

    private static double MeanDifference(byte[] a, byte[] b) => a.Zip(b).Average(p => Math.Abs(p.First - p.Second));

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly Lock _lock = new();

        public void Report(T value)
        {
            lock (_lock)
                report(value);
        }
    }
}
