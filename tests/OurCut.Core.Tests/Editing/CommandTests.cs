using OurCut.Core.Editing;
using OurCut.Core.Editing.Commands;
using OurCut.Core.Model;

namespace OurCut.Core.Tests.Editing;

public class CommandTests
{
    private static readonly Project P = Sample.Project;

    [Fact]
    public void Add_appends_with_the_next_id_and_a_default_label()
    {
        var after = new AddClipCommand(100, 110).Apply(P);
        var clip = after.Clips[^1];
        Assert.Equal(new Clip(7, "Clip 7", 100, 110), clip);
        Assert.Equal("Added clip 7", new AddClipCommand(100, 110).Describe(P));
    }

    [Fact]
    public void Add_can_insert_at_a_position_with_an_explicit_id()
    {
        var after = new AddClipCommand(50, 60, "Break", Index: 1, Id: 42).Apply(P);
        Assert.Equal(new Clip(42, "Break", 50, 60), after.Clips[1]);
        Assert.Throws<EditException>(() => new AddClipCommand(50, 60, Id: 3).Apply(P));
        Assert.Throws<EditException>(() => new AddClipCommand(50, 60, Index: 9).Apply(P));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(10, 10.1)]
    [InlineData(20, 10)]
    [InlineData(860, 900)]
    [InlineData(double.NaN, 10)]
    public void Invalid_ranges_are_rejected(double start, double end) =>
        Assert.Throws<EditException>(() => new AddClipCommand(start, end).Apply(P));

    [Fact]
    public void Remove_deletes_the_clip()
    {
        var after = new RemoveClipCommand(3).Apply(P);
        Assert.Null(after.Find(3));
        Assert.Equal(5, after.Clips.Count);
        Assert.Equal("Removed clip 3", new RemoveClipCommand(3).Describe(P));
        Assert.Throws<EditException>(() => new RemoveClipCommand(99).Apply(P));
    }

    [Fact]
    public void Set_range_changes_times_and_describes_which_end_moved()
    {
        Assert.Equal(123.14, new SetClipRangeCommand(2, 123.14, 190.12).Apply(P).Get(2).Start);
        Assert.Equal("Trimmed clip 2 in-point", new SetClipRangeCommand(2, 123.14, 190.12).Describe(P));
        Assert.Equal("Trimmed clip 6 out-point", new SetClipRangeCommand(5, 750, 812.4).Describe(P));
        Assert.Equal("Set clip 1 to 00:10.000 – 00:20.000", new SetClipRangeCommand(1, 10, 20).Describe(P));
    }

    [Fact]
    public void Commands_that_change_nothing_return_the_same_project()
    {
        Assert.Same(P, new SetClipRangeCommand(1, 12.04, 45.32).Apply(P));
        Assert.Same(P, new SetClipIncludedCommand(1, true).Apply(P));
        Assert.Same(P, new MoveClipCommand(1, 0).Apply(P));
        Assert.Same(P, new RenameClipCommand(1, " Intro ").Apply(P));
    }

    [Fact]
    public void Split_keeps_the_first_part_and_inserts_the_second_after_it()
    {
        var after = new SplitClipCommand(3, 301.42).Apply(P);
        Assert.Equal(new Clip(3, "Demo — import", 242.88, 301.42), after.Clips[2]);
        Assert.Equal(new Clip(7, "Demo — import (b)", 301.42, 404.0), after.Clips[3]);
        Assert.Equal(P.OutputDuration, after.OutputDuration, 6);
    }

    [Theory]
    [InlineData(242.9)]
    [InlineData(403.9)]
    [InlineData(500)]
    public void Split_must_be_inside_the_clip(double at) =>
        Assert.Throws<EditException>(() => new SplitClipCommand(3, at).Apply(P));

    [Fact]
    public void Split_of_an_excluded_clip_keeps_both_parts_excluded()
    {
        var after = new SplitClipCommand(6, 700).Apply(P);
        Assert.All(after.Clips.Where(c => c.Label.StartsWith("Q&A", StringComparison.Ordinal)), c => Assert.False(c.IsIncluded));
    }

    [Fact]
    public void Set_included_excludes_without_removing()
    {
        var after = new SetClipIncludedCommand(3, false).Apply(P);
        Assert.False(after.Get(3).IsIncluded);
        Assert.Equal(6, after.Clips.Count);
        Assert.Equal("Excluded clip 3", new SetClipIncludedCommand(3, false).Describe(P));
        Assert.Equal("Kept clip 5", new SetClipIncludedCommand(6, true).Describe(P));
    }

    [Fact]
    public void Move_reorders_the_output()
    {
        var after = new MoveClipCommand(1, 2).Apply(P);
        Assert.Equal([2, 3, 1, 4, 6, 5], after.Clips.Select(c => c.Id));
        Assert.Equal("Moved clip 1 to position 3", new MoveClipCommand(1, 2).Describe(P));
        Assert.Throws<EditException>(() => new MoveClipCommand(1, 6).Apply(P));
    }

    [Fact]
    public void Rename_trims_and_rejects_empty_names()
    {
        Assert.Equal("Opening", new RenameClipCommand(1, "  Opening ").Apply(P).Get(1).Label);
        Assert.Throws<EditException>(() => new RenameClipCommand(1, "   ").Apply(P));
    }

    [Fact]
    public void Batch_applies_all_commands_in_order()
    {
        var batch = new BatchCommand("trim_segment", "Removed silence 3×",
        [
            new SetClipRangeCommand(1, 8.1, 45.32),
            new SetClipRangeCommand(3, 242.88, 410.6),
            new SetClipRangeCommand(5, 745.2, 828.72),
        ]);
        var after = batch.Apply(P);
        Assert.Equal(P.OutputDuration + 3.94 + 6.6 + 4.8, after.OutputDuration, 6);
        Assert.Equal("Removed silence 3×", batch.Describe(P));
    }

    [Fact]
    public void A_failing_batch_leaves_the_original_project_untouched()
    {
        var batch = new BatchCommand("x", "x", [new RemoveClipCommand(1), new RemoveClipCommand(99)]);
        Assert.Throws<EditException>(() => batch.Apply(P));
        Assert.Equal(6, P.Clips.Count);
    }
}
