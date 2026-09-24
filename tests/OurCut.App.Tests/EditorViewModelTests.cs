using Avalonia.Headless.XUnit;
using OurCut.App.Demo;
using OurCut.App.ViewModels;
using OurCut.Core.Editing;
using OurCut.Core.Model;

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
    public void Opening_a_project_refreshes_everything_derived_from_its_source()
    {
        var editor = App.CreateEditor(null);
        var changed = new HashSet<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        DemoScenario.OpenSample(editor);

        Assert.Equal("00:14:32.480", editor.DurationText);
        Assert.Equal("14:32", editor.SourceLengthText);
        Assert.Superset(new HashSet<string?> { "DurationText", "SourceLengthText", "HasFile", "ExcludedText", "KeptText", "StatusRight" }, changed);
        Assert.Equal("lossless copy · snap on · not saved", editor.StatusRight);
    }

    [AvaloniaFact]
    public void Excluded_gaps_are_the_source_ranges_outside_all_clips()
    {
        var editor = Sample();
        var gaps = editor.ExcludedGaps();
        Assert.Equal(7, gaps.Count);
        Assert.Equal(new TimeRange(0, 12.04), gaps[0]);
        Assert.Equal(new TimeRange(45.32, 118.6), gaps[1]);
        Assert.Equal(new TimeRange(828.72, 872.48), gaps[^1]);
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
    public void Trim_keeps_the_minimum_clip_length()
    {
        var editor = Sample();
        var clip = editor.Clips[0];
        editor.Trim(clip, inPoint: true, clip.End + 10);
        Assert.Equal(clip.End - EditRules.MinClipDuration, clip.Start, 6);
        editor.Trim(clip, inPoint: false, 0);
        Assert.Equal(clip.Start + EditRules.MinClipDuration, clip.End, 6);
    }

    [AvaloniaFact]
    public void Trim_snaps_to_keyframes_unless_snapping_is_off()
    {
        var editor = Sample();
        var clip = editor.Clips[1];
        double k = editor.Media!.Keyframes.First(x => x > clip.Start + 1);
        editor.Trim(clip, inPoint: true, k + 0.2, snapThreshold: 0.5);
        Assert.Equal(k, clip.Start);
        editor.SnapToKeyframes = false;
        editor.Trim(clip, inPoint: true, k + 0.2, snapThreshold: 0.5);
        Assert.Equal(k + 0.2, clip.Start, 9);
    }

    [AvaloniaFact]
    public void One_drag_is_one_undo_step()
    {
        var editor = Sample();
        var clip = editor.Clips[0];
        editor.SnapToKeyframes = false;
        foreach (double t in new[] { 11.0, 10.0, 9.0 })
            editor.Trim(clip, inPoint: true, t, mergeKey: "drag");
        Assert.Equal(9, clip.Start);
        editor.Undo();
        Assert.Equal(12.04, clip.Start);
    }

    [AvaloniaFact]
    public void Edits_go_through_the_core_session_and_can_be_undone()
    {
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        var clip = editor.Clips[0];
        editor.Select(clip);

        editor.ToggleExclude();
        Assert.False(clip.IsIncluded);
        Assert.False(editor.Session.Project.Get(clip.Id).IsIncluded);
        Assert.True(editor.CanUndo);

        editor.Undo();
        Assert.True(clip.IsIncluded);
        editor.Redo();
        Assert.False(clip.IsIncluded);
    }

    [AvaloniaFact]
    public void Outside_demo_mode_the_claude_panel_lists_the_edit_history()
    {
        var editor = App.CreateEditor(null);
        DemoScenario.OpenSample(editor);
        editor.Select(editor.Clips[0]);
        editor.ToggleExclude();
        editor.MoveClip(0, 2);

        Assert.Equal(["Excluded clip 1", "Moved clip 1 to position 3"], editor.Claude.Log.Select(a => a.Text));
        Assert.Equal("set_included · −33.28 s", editor.Claude.Log[0].Meta);

        // Undo from the log undoes that entry and everything after it.
        editor.Claude.Log[0].ToggleUndo();
        Assert.All(editor.Claude.Log, a => Assert.True(a.IsUndone));
        Assert.Equal(0, editor.Session.History.Position);
        editor.Claude.Log[1].ToggleUndo();
        Assert.Equal(2, editor.Session.History.Position);
    }

    [AvaloniaFact]
    public void Delete_removes_the_clip_and_selects_the_next_one()
    {
        var editor = Sample();
        var selected = editor.SelectedClip!;
        int index = editor.Clips.IndexOf(selected);
        editor.DeleteClip();
        Assert.DoesNotContain(selected, editor.Clips);
        Assert.Same(editor.Clips[index], editor.SelectedClip);
        editor.Undo();
        Assert.Equal(6, editor.Clips.Count);
    }

    [AvaloniaFact]
    public void Invalid_edits_show_a_message_instead_of_failing()
    {
        var editor = Sample();
        editor.SetTime(editor.Duration - 0.05);
        editor.Select(null);
        editor.MarkIn();
        Assert.Equal(6, editor.Clips.Count);
        Assert.NotNull(editor.StatusMessage);
        Assert.Equal(editor.StatusMessage, editor.StatusRight);
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
