using System.Globalization;
using OurCut.Media.Tools;

namespace OurCut.Media.Probing;

/// <summary>
/// Lists the keyframes of the first video stream by reading packet flags with ffprobe (no decoding).
/// The scan reads the whole file, so it runs in the background and is cached per file.
/// </summary>
public static class KeyframeScanner
{
    /// <summary>Keyframe times in seconds from the file start (start time subtracted), sorted.</summary>
    /// <param name="progress">Fraction of the file scanned, 0..1.</param>
    public static async Task<double[]> ScanAsync(MediaInfo info, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (info.Video is null)
            return [];
        var times = new List<double>();
        double lastReport = 0;
        await ToolProcess.ReadLinesAsync("ffprobe",
            ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time,flags", "-of", "csv=p=0", "-i", info.Path],
            line =>
            {
                if (ParsePacket(line) is not { } packet)
                    return;
                double t = packet.Time - info.StartTime;
                if (packet.IsKey)
                    times.Add(t);
                double f = info.Duration > 0 ? Math.Clamp(t / info.Duration, 0, 1) : 0;
                if (progress is not null && f - lastReport >= 0.01)
                {
                    lastReport = f;
                    progress.Report(f);
                }
            }, cancellationToken).ConfigureAwait(false);
        progress?.Report(1);
        times.Sort();
        return [.. times.Distinct()];
    }

    /// <summary>
    /// Parses one <c>pts_time,dts_time,flags</c> line. Returns null for lines without a usable time.
    /// Discarded packets ('D') are skipped.
    /// </summary>
    public static (double Time, bool IsKey)? ParsePacket(string line)
    {
        var parts = line.Split(',');
        if (parts.Length < 3)
            return null;
        string flags = parts[2];
        if (flags.Contains('D', StringComparison.Ordinal))
            return null;
        if (!TryTime(parts[0], out double t) && !TryTime(parts[1], out t))
            return null;
        return (t, flags.StartsWith('K'));
    }

    private static bool TryTime(string text, out double t) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out t);
}
