using System.Collections.Immutable;

using OurCut.Core.Editing;

namespace OurCut.Core.Model;

/// <summary>A source range, e.g. an excluded gap between clips.</summary>
public readonly record struct TimeRange(double Start, double End)
{
    public double Duration => End - Start;
}

/// <summary>
/// An editing project: one source file and the clips cut from it. The order of <see cref="Clips"/>
/// is the output order. Projects are immutable; edit commands return new instances.
/// </summary>
public sealed record Project(string Name, SourceMedia? Source, ImmutableList<Clip> Clips)
{
    /// <summary>Gaps shorter than this are not reported as excluded ranges.</summary>
    public const double MinimumGap = 0.3;

    public static Project Empty { get; } = new("Untitled project", null, []);

    public double SourceDuration => Source?.Duration ?? 0;

    public IEnumerable<Clip> IncludedClips => Clips.Where(c => c.IsIncluded);

    /// <summary>Length of the export: the included clips back to back.</summary>
    public double OutputDuration => IncludedClips.Sum(c => c.Duration);

    public Clip? Find(int id) => Clips.FirstOrDefault(c => c.Id == id);

    public Clip Get(int id) => Find(id) ?? throw new EditException($"Clip {id} does not exist.");

    public int IndexOf(int id) => Clips.FindIndex(c => c.Id == id);

    /// <summary>1-based position of a clip in the output order, as shown in the UI.</summary>
    public int NumberOf(int id) => IndexOf(id) + 1;

    public int NextClipId => Clips.IsEmpty ? 1 : Clips.Max(c => c.Id) + 1;

    /// <summary>First clip in output order that contains <paramref name="time"/>.</summary>
    public Clip? ClipAt(double time) => Clips.FirstOrDefault(c => c.Contains(time));

    /// <summary>Source ranges not covered by any clip (included or not), in source order.</summary>
    public IReadOnlyList<TimeRange> UncoveredRanges()
    {
        var gaps = new List<TimeRange>();
        double last = 0;
        foreach (var c in Clips.OrderBy(c => c.Start))
        {
            if (c.Start - last > MinimumGap)
                gaps.Add(new TimeRange(last, c.Start));
            last = Math.Max(last, c.End);
        }
        if (SourceDuration - last > MinimumGap)
            gaps.Add(new TimeRange(last, SourceDuration));
        return gaps;
    }

    /// <summary>Everything that will not be exported: uncovered gaps plus excluded clips.</summary>
    public double ExcludedDuration =>
        UncoveredRanges().Sum(g => g.Duration) + Clips.Where(c => !c.IsIncluded).Sum(c => c.Duration);
}
