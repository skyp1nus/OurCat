using OurCut.Core.Model;
using OurCut.Media.Export;
using static OurCut.Media.Tests.Export.ExportSample;

namespace OurCut.Media.Tests.Export;

public class ExportPlannerTests
{
    [Fact]
    public void Lossless_merge_cuts_each_clip_to_a_temp_file_then_concatenates()
    {
        var plan = Plan(Settings());

        Assert.Equal([Out("demo-cut.mp4")], plan.Outputs);
        Assert.Equal([ExportStepKind.Cut, ExportStepKind.Cut, ExportStepKind.Concat], plan.Steps.Select(s => s.Kind));
        Assert.Equal(Out(".ourcut-tmp-t1-001.mp4"), plan.Steps[0].OutputPath);
        Assert.Equal(Out(".ourcut-tmp-t1-002.mp4"), plan.Steps[1].OutputPath);
        Assert.True(plan.Steps[0].IsTemporary);
        Assert.False(plan.Steps[2].IsTemporary);
        Assert.Equal(Out("demo-cut.mp4"), plan.Steps[2].OutputPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffconcat"), plan.ConcatListPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffmeta"), plan.ChaptersPath);
        Assert.Equal(
            [Out(".ourcut-tmp-t1-001.mp4"), Out(".ourcut-tmp-t1-002.mp4"), Out(".ourcut-tmp-t1.ffconcat"), Out(".ourcut-tmp-t1.ffmeta")],
            plan.TemporaryFiles);
    }

    [Fact]
    public void Excluded_clips_are_not_exported() =>
        Assert.Equal([1, 2], Plan(Settings()).Clips.Select(c => c.ClipId));

    [Fact]
    public void Lossless_clips_start_at_the_previous_keyframe()
    {
        var plan = Plan(Settings());
        Assert.Equal(1.0, plan.Clips[0].OutputStart);
        Assert.Equal(2.2, plan.Clips[0].OutputDuration, 9);
        Assert.Equal(5.0, plan.Clips[1].OutputStart);
        Assert.Equal(4.2, plan.OutputDuration, 9);
    }

    [Fact]
    public void Step_weights_add_up_to_one()
    {
        foreach (var settings in new[]
                 {
                     Settings(), Settings(merge: false), Settings(CutMode.Reencode), Settings(CutMode.Reencode, merge: false),
                 })
        {
            Assert.Equal(1.0, Plan(settings).Steps.Sum(s => s.Weight), 9);
        }
    }

    [Fact]
    public void Cut_weights_follow_clip_duration()
    {
        var steps = Plan(Settings()).Steps;
        Assert.Equal((1 - ExportPlanner.ConcatWeight) * 2.2 / 4.2, steps[0].Weight, 9);
        Assert.Equal(ExportPlanner.ConcatWeight, steps[2].Weight, 9);
    }

    [Fact]
    public void Separate_files_are_named_after_their_clips()
    {
        var plan = Plan(Settings(merge: false));
        Assert.Equal([Out("demo-1-intro.mp4"), Out("demo-2-demo-import.mp4")], plan.Outputs);
        Assert.All(plan.Steps, s => Assert.Equal(ExportStepKind.Cut, s.Kind));
        Assert.All(plan.Steps, s => Assert.False(s.IsTemporary));
        Assert.Null(plan.ConcatListPath);
        Assert.Null(plan.ChaptersPath);
        Assert.Empty(plan.TemporaryFiles);
    }

    [Fact]
    public void Reencoded_merge_is_one_step_with_exact_cut_points()
    {
        var plan = Plan(Settings(CutMode.Reencode));
        var step = Assert.Single(plan.Steps);
        Assert.Equal(ExportStepKind.EncodeMerged, step.Kind);
        Assert.Equal(1, step.Weight);
        Assert.All(plan.Clips, c => Assert.Null(c.Lossless));
        Assert.Equal(1.5, plan.Clips[0].OutputStart);
        Assert.Equal(3.7, plan.OutputDuration, 9);
        Assert.Null(plan.ConcatListPath);
        Assert.Equal(Out(".ourcut-tmp-t1.ffmeta"), plan.ChaptersPath);
    }

    [Fact]
    public void Chapters_can_be_turned_off() =>
        Assert.Null(Plan(Settings() with { AddChapters = false }).ChaptersPath);

    [Fact]
    public void Container_decides_the_extension()
    {
        var plan = Plan(Settings() with { Container = OutputContainer.Mkv });
        Assert.Equal([Out("demo-cut.mkv")], plan.Outputs);
        Assert.Equal(Out(".ourcut-tmp-t1-001.mkv"), plan.Steps[0].OutputPath);
    }

    [Fact]
    public void Existing_files_are_not_overwritten()
    {
        var taken = new HashSet<string> { Out("demo-cut.mp4"), Out("demo-cut (2).mp4") };
        Assert.Equal([Out("demo-cut (3).mp4")], Plan(Settings(), fileExists: taken.Contains).Outputs);
    }

    [Fact]
    public void Separate_outputs_never_collide_with_each_other()
    {
        var project = SampleProject with
        {
            Clips = [new Clip(1, "Take", 1, 2), new Clip(2, "Take", 3, 4)],
        };
        var taken = new HashSet<string> { Out("demo-2-take.mp4") };
        var plan = Plan(Settings(merge: false) with { BaseName = "demo" }, project, fileExists: taken.Contains);
        Assert.Equal([Out("demo-1-take.mp4"), Out("demo-2-take (2).mp4")], plan.Outputs);
    }

    [Fact]
    public void Nothing_included_is_an_error()
    {
        var project = SampleProject with { Clips = [new Clip(1, "A", 1, 2, IsIncluded: false)] };
        Assert.Throws<InvalidOperationException>(() => Plan(Settings(), project));
    }

    [Fact]
    public void Smart_cut_is_not_available_yet() =>
        Assert.Throws<InvalidOperationException>(() => Plan(Settings(CutMode.SmartCut)));

    [Fact]
    public void An_export_never_overwrites_its_source()
    {
        var info = Info with { Path = Out("demo-cut.mp4") };
        var ex = Assert.Throws<InvalidOperationException>(() => Plan(Settings(), info: info));
        Assert.Contains("source", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Temp_ids_are_random_by_default()
    {
        var a = ExportPlanner.Plan(SampleProject, Info, Keyframes, Settings(), _ => false);
        var b = ExportPlanner.Plan(SampleProject, Info, Keyframes, Settings(), _ => false);
        Assert.NotEqual(a.Steps[0].OutputPath, b.Steps[0].OutputPath);
        Assert.StartsWith(".ourcut-tmp-", Path.GetFileName(a.Steps[0].OutputPath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Intro", "intro")]
    [InlineData("Demo — import", "demo-import")]
    [InlineData("Q&A", "q-a")]
    [InlineData("  Take 2!  ", "take-2")]
    [InlineData("Вступне слово", "вступне-слово")]
    [InlineData("?!", "clip")]
    [InlineData("", "clip")]
    public void Slug_makes_labels_file_name_friendly(string label, string expected) =>
        Assert.Equal(expected, ExportPlanner.Slug(label));
}
