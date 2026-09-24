using OurCut.Core.Model;

namespace OurCut.Core.Editing;

/// <summary>
/// One edit of a project. Every change to a project goes through a command, so the UI, undo/redo
/// and a future MCP server all use the same operations.
/// </summary>
public interface IEditCommand
{
    /// <summary>Machine-readable name, e.g. <c>trim_segment</c>. Doubles as the future MCP tool name.</summary>
    string Name { get; }

    /// <summary>Human-readable summary for the history, e.g. "Trimmed clip 2 in-point".</summary>
    /// <param name="before">The project the command is applied to.</param>
    string Describe(Project before);

    /// <summary>Returns the edited project. Returns <paramref name="project"/> itself if nothing changes.</summary>
    /// <exception cref="EditException">The edit is not valid for this project.</exception>
    Project Apply(Project project);
}

/// <summary>Who made an edit. Assistant edits are highlighted in the UI.</summary>
public enum EditOrigin
{
    User,
    Assistant,
}

/// <summary>Which end of a clip an edit moves.</summary>
public enum ClipEdge
{
    In,
    Out,
}

/// <summary>Limits shared by all commands.</summary>
public static class EditRules
{
    /// <summary>Shortest allowed clip, in seconds.</summary>
    public const double MinClipDuration = 0.2;

    /// <summary>Tolerance for comparing times, in seconds.</summary>
    public const double Epsilon = 1e-9;

    public static void ValidateRange(Project project, double start, double end)
    {
        if (double.IsNaN(start) || double.IsNaN(end) || double.IsInfinity(start) || double.IsInfinity(end))
            throw new EditException("Clip times must be finite numbers.");
        if (start < -Epsilon)
            throw new EditException($"In-point {start:0.###} s is before the start of the source.");
        if (project.Source is { } source && end > source.Duration + Epsilon)
            throw new EditException($"Out-point {end:0.###} s is after the end of the source ({source.Duration:0.###} s).");
        if (end - start < MinClipDuration - Epsilon)
            throw new EditException($"Clips must be at least {MinClipDuration} s long.");
    }
}
