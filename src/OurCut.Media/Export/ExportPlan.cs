using System.Text.RegularExpressions;
using OurCut.Core.Model;
using OurCut.Media.Probing;

namespace OurCut.Media.Export;

/// <summary>One included clip as it will be exported.</summary>
/// <param name="Number">1-based position among the exported clips.</param>
/// <param name="Lossless">Cut points for stream copy; null when re-encoding.</param>
public sealed record ExportClip(int ClipId, int Number, string Label, double Start, double End, LosslessCut? Lossless)
{
    /// <summary>Where the output of this clip starts in the source (earlier than <see cref="Start"/> for lossless cuts).</summary>
    public double OutputStart => Lossless?.EffectiveStart ?? Start;

    public double OutputDuration => End - OutputStart;
}

public enum ExportStepKind
{
    /// <summary>Stream-copy one clip.</summary>
    Cut,

    /// <summary>Join the cut clips with the concat demuxer.</summary>
    Concat,

    /// <summary>Re-encode one clip.</summary>
    Encode,

    /// <summary>Re-encode all clips into one file in a single pass.</summary>
    EncodeMerged,
}

/// <param name="Weight">Share of the whole export's progress, 0..1.</param>
public sealed record ExportStep(ExportStepKind Kind, string Name, string OutputPath, bool IsTemporary, double Duration,
    IReadOnlyList<ExportClip> Clips, double Weight);

/// <summary>Everything an export will do, decided up front so it can be shown and tested.</summary>
public sealed record ExportPlan(
    MediaInfo Source,
    ExportSettings Settings,
    IReadOnlyList<ExportClip> Clips,
    IReadOnlyList<ExportStep> Steps,
    IReadOnlyList<string> Outputs,
    string? ConcatListPath,
    string? ChaptersPath)
{
    /// <summary>Temporary files the export creates and removes.</summary>
    public IEnumerable<string> TemporaryFiles =>
        Steps.Where(s => s.IsTemporary).Select(s => s.OutputPath)
            .Concat(new[] { ConcatListPath, ChaptersPath }.OfType<string>());

    public double OutputDuration => Clips.Sum(c => c.OutputDuration);
}

/// <summary>Turns a project and export settings into an <see cref="ExportPlan"/>.</summary>
public static partial class ExportPlanner
{
    /// <summary>Share of progress given to the final concat step of a lossless merge.</summary>
    public const double ConcatWeight = 0.08;

    /// <exception cref="InvalidOperationException">Nothing to export, or the settings are not supported.</exception>
    /// <param name="fileExists">Checks whether an output name is taken (tests pass a fake).</param>
    /// <param name="tempId">Distinguishes this export's temporary files; random by default.</param>
    public static ExportPlan Plan(Project project, MediaInfo source, IReadOnlyList<double> keyframes, ExportSettings settings,
        Func<string, bool>? fileExists = null, string? tempId = null)
    {
        fileExists ??= File.Exists;
        if (settings.Mode == CutMode.SmartCut)
            throw new InvalidOperationException("Smart cut is not available yet.");
        var included = project.IncludedClips.ToList();
        if (included.Count == 0)
            throw new InvalidOperationException("There are no clips to export. Include at least one clip.");

        bool lossless = settings.Mode == CutMode.Lossless;
        var clips = included.Select((c, i) => new ExportClip(c.Id, i + 1, c.Label, c.Start, c.End,
            lossless
                ? CutPlanner.Plan(c.Start, c.End, keyframes, source.Family, source.Video?.HasBFrames ?? false,
                    source.Video?.FrameDuration ?? 1 / 30.0)
                : null)).ToList();

        string folder = settings.OutputFolder;
        string ext = settings.Extension;
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string fileName) => UniquePath(Path.Combine(folder, fileName), p => fileExists(p) || reserved.Contains(p), reserved);

        tempId ??= Guid.NewGuid().ToString("N")[..12];
        string Temp(string suffix) => Path.Combine(folder, $".ourcut-tmp-{tempId}{suffix}");

        var steps = new List<ExportStep>();
        var outputs = new List<string>();
        string? listPath = null, chaptersPath = null;
        double total = clips.Sum(c => c.OutputDuration);

        if (settings.Merge)
        {
            string final = Unique($"{settings.BaseName}-cut.{ext}");
            outputs.Add(final);
            if (settings.AddChapters)
                chaptersPath = Temp(".ffmeta");
            if (lossless)
            {
                foreach (var c in clips)
                {
                    steps.Add(new ExportStep(ExportStepKind.Cut, $"{c.Number} · {c.Label}", Temp($"-{c.Number:000}.{ext}"), true,
                        c.OutputDuration, [c], (1 - ConcatWeight) * Share(c.OutputDuration, total, clips.Count)));
                }
                listPath = Temp(".ffconcat");
                steps.Add(new ExportStep(ExportStepKind.Concat, $"Merge into {Path.GetFileName(final)}", final, false, total, clips,
                    ConcatWeight));
            }
            else
            {
                steps.Add(new ExportStep(ExportStepKind.EncodeMerged, $"Merge into {Path.GetFileName(final)}", final, false, total, clips, 1));
            }
        }
        else
        {
            foreach (var c in clips)
            {
                string final = Unique($"{settings.BaseName}-{c.Number}-{Slug(c.Label)}.{ext}");
                outputs.Add(final);
                steps.Add(new ExportStep(lossless ? ExportStepKind.Cut : ExportStepKind.Encode, Path.GetFileName(final), final, false,
                    c.OutputDuration, [c], Share(c.OutputDuration, total, clips.Count)));
            }
        }

        if (outputs.Any(o => string.Equals(Path.GetFullPath(o), Path.GetFullPath(source.Path), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The export would overwrite the source file.");

        return new ExportPlan(source, settings, clips, steps, outputs, listPath, chaptersPath);
    }

    /// <summary>
    /// Lower-case file-name friendly form of a clip label, e.g. "Demo — import" → "demo-import".
    /// Letters of any script are kept ("Вступ" → "вступ").
    /// </summary>
    public static string Slug(string text)
    {
        string slug = NonAlphanumeric().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "clip" : slug;
    }

    /// <summary>Adds " (2)", " (3)", … before the extension until the path is free.</summary>
    internal static string UniquePath(string path, Func<string, bool> taken, HashSet<string> reserved)
    {
        string candidate = path;
        string dir = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string ext = Path.GetExtension(path);
        for (int n = 2; taken(candidate); n++)
            candidate = Path.Combine(dir, $"{name} ({n}){ext}");
        reserved.Add(candidate);
        return candidate;
    }

    private static double Share(double duration, double total, int count) => total > 0 ? duration / total : 1.0 / count;

    [GeneratedRegex(@"[^\p{L}\p{Nd}]+")]
    private static partial Regex NonAlphanumeric();
}
