using System.Runtime.InteropServices;
using OurCut.Media.Playback;
using OurCut.Media.Tests.Integration;

namespace OurCut.Media.Tests.Playback;

public class AudioMixTests
{
    [Fact]
    public void One_enabled_track_is_played_directly() =>
        Assert.Equal(new AudioMix("2", ""), AudioMix.For([false, true, false]));

    [Fact]
    public void Several_tracks_are_mixed() =>
        Assert.Equal(new AudioMix("auto", "[aid1][aid3]amix=inputs=2:normalize=0[ao]"), AudioMix.For([true, false, true]));

    [Fact]
    public void All_muted_plays_no_audio() =>
        Assert.Equal(new AudioMix("no", ""), AudioMix.For([false, false]));

    [Fact]
    public void A_file_without_audio_has_nothing_to_play() =>
        Assert.Equal(new AudioMix("no", ""), AudioMix.For([]));
}

/// <summary>Plays generated media with libmpv (audio discarded). Skipped without libmpv or ffmpeg.</summary>
public sealed class MpvPlayerTests(SampleMediaFixture media) : IClassFixture<SampleMediaFixture>, IDisposable
{
    private MpvPlayer? _player;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _player?.Dispose();

    private async Task<MpvPlayer> LoadAsync(string? path = null)
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });
        await _player.LoadAsync(path ?? media.Mp4).WaitAsync(TimeSpan.FromSeconds(20), Ct);
        await WaitUntil(() => _player.Duration > 0);
        return _player;
    }

    private static async Task WaitUntil(Func<bool> condition, double seconds = 10)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for mpv.");
            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task Loading_reports_the_file_paused_at_the_start()
    {
        var player = await LoadAsync();
        Assert.Equal(10, player.Duration, 1);
        Assert.Equal(media.Mp4, player.LoadedPath);
        Assert.False(player.IsPlaying);
        Assert.Equal(0, player.Position, 2);
        await WaitUntil(() => player.VideoSize.Width > 0);
        Assert.Equal((320, 180), player.VideoSize);
    }

    [Fact]
    public async Task Seeks_are_frame_exact()
    {
        var player = await LoadAsync();
        player.Seek(4.5);
        Assert.True(player.IsSeeking);
        Assert.Equal(4.5, player.Position, 6);
        await WaitUntil(() => !player.IsSeeking);
        Assert.Equal(4.5, player.Position, 3);

        // Rapid seeks while dragging: only the last one settles.
        player.Seek(1.0);
        player.Seek(2.0);
        player.Seek(7.2);
        await WaitUntil(() => !player.IsSeeking);
        Assert.Equal(7.2, player.Position, 2);
    }

    [Fact]
    public async Task Frame_steps_move_one_frame()
    {
        var player = await LoadAsync();
        player.Seek(3.0);
        await WaitUntil(() => !player.IsSeeking);

        player.StepFrame(forward: true);
        await WaitUntil(() => player.Position > 3.01);
        Assert.Equal(3 + 1 / 30.0, player.Position, 3);

        player.StepFrame(forward: false);
        await WaitUntil(() => player.Position < 3.01);
        Assert.Equal(3.0, player.Position, 3);
        Assert.False(player.IsPlaying);
    }

    [Fact]
    public async Task Play_advances_and_pause_stops()
    {
        var player = await LoadAsync();
        int changes = 0;
        player.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        player.Play();
        await WaitUntil(() => player.IsPlaying);
        await WaitUntil(() => player.Position > 0.3);
        player.Pause();
        await WaitUntil(() => !player.IsPlaying);
        double stopped = player.Position;
        await Task.Delay(200, Ct);

        Assert.Equal(stopped, player.Position, 3);
        Assert.True(changes > 3);
    }

    [Fact]
    public async Task Playback_stops_on_the_last_frame()
    {
        var player = await LoadAsync();
        player.SetSpeed(4);
        player.Seek(9.0);
        player.Play();
        await WaitUntil(() => player.IsEndReached);
        Assert.False(player.IsPlaying);
        Assert.InRange(player.Position, 9.8, 10.0);
    }

    [Fact]
    public async Task Muting_lanes_changes_the_audio_mix()
    {
        var player = await LoadAsync();
        player.SetAudioTracks([true, true]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "[aid1][aid2]amix=inputs=2:normalize=0[ao]");

        player.SetAudioTracks([false, true]);
        await WaitUntil(() => player.GetPropertyString("lavfi-complex") == "" && player.GetPropertyString("aid") == "2");

        player.SetAudioTracks([false, false]);
        await WaitUntil(() => player.GetPropertyString("aid") == "no");
    }

    [Fact]
    public async Task A_file_mpv_cannot_play_fails_to_load()
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        string path = Path.Combine(media.Folder, "not-a-video.mp4");
        await File.WriteAllTextAsync(path, "nope", Ct);
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null" });

        await Assert.ThrowsAsync<MpvException>(() => _player.LoadAsync(path).WaitAsync(TimeSpan.FromSeconds(20), Ct));
        Assert.Null(_player.LoadedPath);
    }

    [Fact]
    public async Task Attaching_a_renderer_while_a_file_loads_does_not_cancel_the_load()
    {
        media.SkipIfUnavailable();
        Assert.SkipUnless(MpvPlayer.IsAvailable(out string? error), error ?? "");
        _player = new MpvPlayer(new MpvPlayerOptions { AudioOutput = "null", HardwareDecoding = "no" });

        var load = _player.LoadAsync(media.Mp4);
        using var renderer = new MpvSoftwareRenderer(_player);
        await load.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        await WaitUntil(() => _player.LoadedPath == media.Mp4 && _player.Duration > 0);
        await WaitUntil(() => _player.GetPropertyString("current-vo") == "libmpv");
    }

    [Fact]
    public async Task The_software_renderer_draws_the_current_frame()
    {
        var player = await LoadAsync();
        player.Seek(2.0);
        await WaitUntil(() => !player.IsSeeking);

        // Attached after loading: the player reloads the file so video output can start.
        using var renderer = new MpvSoftwareRenderer(player);
        int updates = 0;
        renderer.UpdateRequested += (_, _) => Interlocked.Increment(ref updates);
        await WaitUntil(() => Volatile.Read(ref updates) > 0);
        await WaitUntil(() => !player.IsSeeking && player.LoadedPath is not null);

        const int w = 320, h = 180, stride = w * 4;
        IntPtr buffer = Marshal.AllocHGlobal(stride * h);
        try
        {
            var pixels = new byte[stride * h];
            await WaitUntil(() =>
            {
                renderer.HasNewFrame();
                renderer.Render(buffer, w, h, stride);
                Marshal.Copy(buffer, pixels, 0, pixels.Length);
                return pixels.Where((_, i) => i % 4 != 3).Average(b => b) > 40;
            });
            Assert.Equal(2.0, player.Position, 2);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
