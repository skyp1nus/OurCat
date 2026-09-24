using Avalonia.Headless.XUnit;
using OurCut.App.Demo;
using OurCut.App.ViewModels;

namespace OurCut.App.Tests;

public class EditorViewModelTests
{
    private static EditorViewModel Sample() => App.CreateEditor(DesignScreen.Editing);

    [AvaloniaFact]
    public void Totals_match_the_design()
    {
        var editor = Sample();
        Assert.Equal("7:32.000", editor.TotalText);
        Assert.Equal("7:32", editor.KeptText);
        Assert.Equal("7:00", editor.ExcludedText);
        Assert.Equal("5 of 6 clips", editor.ClipSummary);
        Assert.Equal("14:32", editor.SourceLengthText);
    }

    [AvaloniaFact]
    public void Excluded_gaps_are_the_source_ranges_outside_all_clips()
    {
        var editor = Sample();
        var gaps = editor.ExcludedGaps();
        Assert.Equal(7, gaps.Count);
        Assert.Equal((0, 12.04), gaps[0]);
        Assert.Equal((45.32, 118.6), gaps[1]);
        Assert.Equal((828.72, 872.48), gaps[^1]);
    }

    [AvaloniaFact]
    public void Keep_range_inserts_a_clip_in_source_order()
    {
        var editor = Sample();
        editor.KeepRange(45.32, 118.6);
        var added = editor.SelectedClip!;
        Assert.Equal(1, editor.Clips.IndexOf(added));
        Assert.Equal("Clip 7", added.Label);
        Assert.Equal(6, editor.ExcludedGaps().Count);
    }

    [AvaloniaFact]
    public void Trim_keeps_at_least_half_a_second()
    {
        var editor = Sample();
        var clip = editor.Clips[0];
        editor.Trim(clip, inPoint: true, clip.End + 10);
        Assert.Equal(clip.End - 0.5, clip.Start, 6);
        editor.Trim(clip, inPoint: false, 0);
        Assert.Equal(clip.Start + 0.5, clip.End, 6);
    }

    [AvaloniaFact]
    public void Snapping_picks_the_nearest_keyframe_within_the_threshold()
    {
        var editor = Sample();
        double k = editor.Media!.Keyframes[10];
        Assert.Equal(k, editor.SnapToKeyframe(k + 0.2, 0.5));
        Assert.Equal(k + 0.9, editor.SnapToKeyframe(k + 0.9, 0.1));
    }

    [AvaloniaFact]
    public void Claude_undo_restores_the_previous_values_and_clears_the_highlight()
    {
        var editor = App.CreateEditor(DesignScreen.Ai);
        var setup = editor.Clips.Single(c => c.Label == "Setup");
        Assert.True(setup.IsAiChanged);
        Assert.Equal("−20.86 s by Claude", editor.ClaudeDeltaText);

        var trim = editor.Claude.Log.Single(a => a.Text == "Trimmed clip 2 in-point");
        trim.ToggleUndo();

        Assert.Equal(118.6, setup.Start);
        Assert.False(setup.IsAiChanged);
        Assert.Equal("−16.32 s by Claude", editor.ClaudeDeltaText);
        Assert.Equal("Redo", trim.UndoLabel);
    }

    [AvaloniaFact]
    public void Export_paths_and_steps_follow_the_settings()
    {
        var editor = Sample();
        var export = editor.Export;
        export.Open();
        Assert.Equal(@"C:\Users\You\Videos\Exports\launch-keynote-cut.mp4", export.OutputPath);
        Assert.Equal("1 file", export.FileCountText);

        export.Merge = false;
        export.Container = "MKV";
        Assert.Equal("5 files", export.FileCountText);
        Assert.EndsWith(@"launch-keynote-{n}-{label}.mkv", export.OutputPath);

        export.Start(0.5);
        Assert.Equal(5, export.Rows.Count);
        Assert.Equal("launch-keynote-3-demo-import.mkv", export.Rows[2].Name);
        Assert.Single(export.Rows, r => r.IsCurrent);
        export.Close();
    }

    [AvaloniaFact]
    public void Smart_cut_is_listed_but_cannot_be_picked()
    {
        var export = Sample().Export;
        var smart = export.Modes.Single(m => m.Mode == ExportMode.Smart);
        smart.PickCommand.Execute(null);
        Assert.False(smart.IsAvailable);
        Assert.Equal(ExportMode.Copy, export.Mode);
    }
}
