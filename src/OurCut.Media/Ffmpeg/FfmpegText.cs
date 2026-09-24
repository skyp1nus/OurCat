using System.Globalization;

namespace OurCut.Media.Ffmpeg;

/// <summary>Formatting helpers for values passed on the ffmpeg/ffprobe command line.</summary>
public static class FfmpegText
{
    /// <summary>
    /// Seconds with six decimals and an invariant decimal point, e.g. <c>12.040000</c>.
    /// ffmpeg accepts this for -ss, -t and -to without the millisecond truncation of TimeSpan.
    /// </summary>
    public static string Seconds(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds))
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "Time must be a finite number.");
        return Math.Max(0, seconds).ToString("0.000000", CultureInfo.InvariantCulture);
    }
}
