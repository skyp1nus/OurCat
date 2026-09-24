using OurCut.Core.Model;
using OurCut.Core.Time;

namespace OurCut.Core.Editing.Commands;

/// <summary>Adds a clip. Without an index it goes to the end of the output.</summary>
/// <param name="Id">Explicit id (e.g. to restore a removed clip); by default the next free id.</param>
public sealed record AddClipCommand(double Start, double End, string? Label = null, int? Index = null, int? Id = null, bool IsIncluded = true)
    : IEditCommand
{
    public string Name => "add_segment";

    public string Describe(Project before) =>
        $"Added clip {Math.Clamp(Index ?? before.Clips.Count, 0, before.Clips.Count) + 1}";

    public Project Apply(Project project)
    {
        EditRules.ValidateRange(project, Start, End);
        int id = Id ?? project.NextClipId;
        if (project.Find(id) is not null)
            throw new EditException($"Clip {id} already exists.");
        int index = Index ?? project.Clips.Count;
        if (index < 0 || index > project.Clips.Count)
            throw new EditException($"Position {index + 1} is outside the clip list (1–{project.Clips.Count + 1}).");
        var clip = new Clip(id, string.IsNullOrWhiteSpace(Label) ? $"Clip {id}" : Label.Trim(), Start, End, IsIncluded);
        return project with { Clips = project.Clips.Insert(index, clip) };
    }
}

/// <summary>Removes a clip from the project entirely.</summary>
public sealed record RemoveClipCommand(int ClipId) : IEditCommand
{
    public string Name => "remove_segment";

    public string Describe(Project before) => $"Removed clip {before.NumberOf(ClipId)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        return project with { Clips = project.Clips.Remove(clip) };
    }
}

/// <summary>Sets a clip's in- and out-point.</summary>
public sealed record SetClipRangeCommand(int ClipId, double Start, double End) : IEditCommand
{
    public string Name => "trim_segment";

    public string Describe(Project before)
    {
        var clip = before.Get(ClipId);
        int n = before.NumberOf(ClipId);
        bool moveIn = Math.Abs(clip.Start - Start) > EditRules.Epsilon;
        bool moveOut = Math.Abs(clip.End - End) > EditRules.Epsilon;
        return (moveIn, moveOut) switch
        {
            (true, false) => $"Trimmed clip {n} in-point",
            (false, true) => $"Trimmed clip {n} out-point",
            _ => $"Set clip {n} to {TimeFormat.MinutesSeconds(Start)} – {TimeFormat.MinutesSeconds(End)}",
        };
    }

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        EditRules.ValidateRange(project, Start, End);
        if (clip.Start == Start && clip.End == End)
            return project;
        return project with { Clips = project.Clips.Replace(clip, clip with { Start = Start, End = End }) };
    }
}

/// <summary>Splits a clip in two at a source time. The second part is placed right after the first.</summary>
public sealed record SplitClipCommand(int ClipId, double At) : IEditCommand
{
    public string Name => "split_segment";

    public string Describe(Project before) => $"Split clip {before.NumberOf(ClipId)} at {TimeFormat.MinutesSeconds(At)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (At < clip.Start + EditRules.MinClipDuration - EditRules.Epsilon || At > clip.End - EditRules.MinClipDuration + EditRules.Epsilon)
            throw new EditException($"Split point must be at least {EditRules.MinClipDuration} s inside clip {project.NumberOf(ClipId)}.");
        var first = clip with { End = At };
        var second = new Clip(project.NextClipId, clip.Label + " (b)", At, clip.End, clip.IsIncluded);
        int i = project.IndexOf(ClipId);
        return project with { Clips = project.Clips.SetItem(i, first).Insert(i + 1, second) };
    }

    /// <summary>Id the second part will get when applied to <paramref name="project"/>.</summary>
    public static int SecondPartId(Project project) => project.NextClipId;
}

/// <summary>Includes a clip in the export or excludes it (the clip stays in the project).</summary>
public sealed record SetClipIncludedCommand(int ClipId, bool IsIncluded) : IEditCommand
{
    public string Name => "set_included";

    public string Describe(Project before) =>
        $"{(IsIncluded ? "Kept" : "Excluded")} clip {before.NumberOf(ClipId)}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        return clip.IsIncluded == IsIncluded
            ? project
            : project with { Clips = project.Clips.Replace(clip, clip with { IsIncluded = IsIncluded }) };
    }
}

/// <summary>Moves a clip to another position in the output order (0-based index).</summary>
public sealed record MoveClipCommand(int ClipId, int ToIndex) : IEditCommand
{
    public string Name => "move_segment";

    public string Describe(Project before) => $"Moved clip {before.NumberOf(ClipId)} to position {ToIndex + 1}";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (ToIndex < 0 || ToIndex >= project.Clips.Count)
            throw new EditException($"Position {ToIndex + 1} is outside the clip list (1–{project.Clips.Count}).");
        int from = project.IndexOf(ClipId);
        return from == ToIndex
            ? project
            : project with { Clips = project.Clips.RemoveAt(from).Insert(ToIndex, clip) };
    }
}

/// <summary>Renames a clip.</summary>
public sealed record RenameClipCommand(int ClipId, string Label) : IEditCommand
{
    public string Name => "set_label";

    public string Describe(Project before) => $"Renamed clip {before.NumberOf(ClipId)} to “{Label.Trim()}”";

    public Project Apply(Project project)
    {
        var clip = project.Get(ClipId);
        if (string.IsNullOrWhiteSpace(Label))
            throw new EditException("Clip names cannot be empty.");
        string label = Label.Trim();
        return clip.Label == label
            ? project
            : project with { Clips = project.Clips.Replace(clip, clip with { Label = label }) };
    }
}

/// <summary>Several commands applied as one undo step, e.g. "Removed silence 3×".</summary>
public sealed record BatchCommand(string Name, string Description, IReadOnlyList<IEditCommand> Commands) : IEditCommand
{
    public string Describe(Project before) => Description;

    public Project Apply(Project project)
    {
        foreach (var command in Commands)
            project = command.Apply(project);
        return project;
    }
}
