using Avalonia;
using Avalonia.Headless.XUnit;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.Core.Serialization;

namespace OurCut.App.Tests;

/// <summary>Opening videos and projects, with a fake opener (no ffmpeg needed).</summary>
public sealed class MediaOpeningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ourcut-open").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewFile(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [AvaloniaFact]
    public async Task Opening_a_video_starts_an_empty_project_named_after_it()
    {
        var recent = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        var editor = App.CreateEditor(null, new SampleOpener(), recent);
        string video = NewFile("interview take 2.mp4");

        await editor.OpenMediaAsync(video);

        Assert.True(editor.HasFile);
        Assert.Equal("interview take 2", editor.ProjectName);
        Assert.Empty(editor.Clips);
        Assert.Null(editor.ProjectPath);
        Assert.Equal(["Stereo"], editor.AudioLanes.Select(l => l.Label));
        var entry = Assert.Single(editor.RecentFiles);
        Assert.Equal(("interview take 2.mp4", video, "14:32", "Today"), (entry.Name, entry.Path, entry.DurationText, entry.WhenText));
    }

    [AvaloniaFact]
    public async Task Opening_from_the_demo_leaves_demo_mode()
    {
        var editor = App.CreateEditor(DesignScreen.Empty, new SampleOpener());
        Assert.True(editor.IsDemo);

        await editor.OpenMediaAsync(NewFile("clip.mov"));

        Assert.False(editor.IsDemo);
        Assert.False(editor.Claude.IsConnected);
        Assert.Empty(editor.RecentFiles);
    }

    [AvaloniaFact]
    public async Task Open_file_on_the_empty_demo_screen_loads_the_sample()
    {
        var editor = App.CreateEditor(DesignScreen.Empty, new SampleOpener());
        Assert.Empty(editor.RecentFiles);
        await editor.OpenFileCommand.ExecuteAsync(null);
        Assert.True(editor.IsDemo);
        Assert.Equal("interview_final_v3", editor.ProjectName);
        Assert.Equal(5, editor.Clips.Count);
    }

    [AvaloniaFact]
    public async Task The_sample_entry_of_the_recent_list_opens_the_sample()
    {
        var editor = App.CreateEditor(DesignScreen.Empty, new SampleOpener());
        await editor.OpenRecentCommand.ExecuteAsync(new RecentFileViewModel("interview_final_v3.mp4", "", "14:32", "Today"));
        Assert.False(editor.IsDemo);
        Assert.Equal(5, editor.Clips.Count);
    }

    [AvaloniaFact]
    public async Task A_missing_file_shows_a_message_and_keeps_the_current_project()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        await editor.OpenMediaAsync(NewFile("first.mp4"));

        await editor.OpenMediaAsync(Path.Combine(_dir, "missing.mp4"));

        Assert.Equal("first", editor.ProjectName);
        Assert.StartsWith("missing.mp4 was not found", editor.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Without_an_opener_videos_cannot_be_opened()
    {
        var editor = App.CreateEditor(null);
        await editor.OpenMediaAsync(NewFile("a.mp4"));
        Assert.False(editor.HasFile);
        Assert.NotNull(editor.StatusMessage);
    }

    [AvaloniaFact]
    public async Task Dropping_a_project_file_opens_the_project_and_its_video()
    {
        var opener = new SampleOpener();
        var editor = App.CreateEditor(null, opener);
        string video = NewFile("keynote_final_4k.mp4");
        string projectPath = Path.Combine(_dir, "keynote" + ProjectFile.Extension);
        await ProjectFile.SaveAsync(DesignSample.Project with { Source = DesignSample.Source with { Path = video } }, projectPath,
            TestContext.Current.CancellationToken);

        await editor.OpenPath(projectPath);

        Assert.Equal(projectPath, editor.ProjectPath);
        Assert.Equal(5, editor.Clips.Count);
        Assert.Equal([video], opener.Opened);
    }

    [AvaloniaFact]
    public async Task A_project_whose_video_is_gone_is_not_opened()
    {
        var editor = App.CreateEditor(null, new SampleOpener());
        string projectPath = Path.Combine(_dir, "old" + ProjectFile.Extension);
        await ProjectFile.SaveAsync(DesignSample.Project with { Source = DesignSample.Source with { Path = Path.Combine(_dir, "missing.mp4") } },
            projectPath, TestContext.Current.CancellationToken);

        await editor.OpenProjectFileAsync(projectPath);

        Assert.False(editor.HasFile);
        Assert.Contains("was not found", editor.StatusMessage, StringComparison.Ordinal);
    }
}

public sealed class RecentFilesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ourcut-recent").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewFile(string name)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    [Fact]
    public void Newest_first_without_duplicates()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "sub", "recent.json"));
        string a = NewFile("a.mp4"), b = NewFile("b.mp4");
        store.Add(a, 10);
        store.Add(b, 20);
        store.Add(a, 11);

        Assert.Equal([(a, 11.0), (b, 20.0)], store.Load().Select(r => (r.Path, r.Duration)));
    }

    [Fact]
    public void Keeps_at_most_eight_files()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        for (int i = 0; i < 12; i++)
            store.Add(NewFile($"{i}.mp4"), i);
        var list = store.Load();
        Assert.Equal(RecentFilesStore.Capacity, list.Count);
        Assert.Equal("11.mp4", Path.GetFileName(list[0].Path));
    }

    [Fact]
    public void Files_that_no_longer_exist_are_skipped()
    {
        var store = new RecentFilesStore(Path.Combine(_dir, "recent.json"));
        string gone = NewFile("gone.mp4"), kept = NewFile("kept.mp4");
        store.Add(gone, 1);
        store.Add(kept, 2);
        File.Delete(gone);
        Assert.Equal([kept], store.Load().Select(r => r.Path));
    }

    [Fact]
    public void A_broken_list_reads_as_empty()
    {
        string file = Path.Combine(_dir, "recent.json");
        File.WriteAllText(file, "[ not json");
        Assert.Empty(new RecentFilesStore(file).Load());
    }

    [Theory]
    [InlineData(0, "Today")]
    [InlineData(1, "Yesterday")]
    [InlineData(5, "Sep 19")]
    [InlineData(400, "Aug 20, 2025")]
    public void When_text_is_relative_for_recent_days(int daysAgo, string expected)
    {
        var now = new DateTime(2026, 9, 24, 15, 0, 0, DateTimeKind.Local);
        var opened = now.AddDays(-daysAgo).ToUniversalTime();
        Assert.Equal(expected, RecentFileViewModel.From(new RecentFile("/v/a.mp4", 872.48, opened), now).WhenText);
    }
}

public class MediaPreviewDrawingTests
{
    [Fact]
    public void Cover_crops_the_middle_of_the_image()
    {
        var src = MediaPreview.Cover(new Size(160, 90), new Size(90, 34));
        Assert.Equal(160, src.Width, 6);
        Assert.Equal(160 * 34 / 90.0, src.Height, 6);
        Assert.Equal((90 - src.Height) / 2, src.Y, 6);
    }

    [Fact]
    public void Fit_letterboxes_inside_the_target()
    {
        var dst = MediaPreview.Fit(new Size(160, 90), new Rect(0, 0, 800, 600));
        Assert.Equal(new Rect(0, 75, 800, 450), dst);
    }

    [Fact]
    public void Desaturate_moves_colours_towards_grey()
    {
        byte[] red = [0, 0, 255, 255];
        byte[] grey = MediaPreview.Desaturate(red, 1);
        Assert.Equal(grey[0], grey[1]);
        Assert.Equal(grey[1], grey[2]);
        Assert.Equal(54, grey[2]);
        Assert.Equal(255, grey[3]);
        byte[] partial = MediaPreview.Desaturate(red, 0.8);
        Assert.True(partial[2] > partial[1]);
    }
}
