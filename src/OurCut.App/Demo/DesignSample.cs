using Avalonia;
using Avalonia.Media;
using OurCut.App.Services;
using OurCut.App.Theme;

namespace OurCut.App.Demo;

/// <summary>The five screens of the design, used for demo mode and UI tests.</summary>
public enum DesignScreen
{
    Empty,
    Editing,
    Ai,
    Export,
    Exporting,
}

/// <summary>
/// The sample project from design/project/OurCutEditor v2.dc.html ("launch-keynote").
/// Waveforms and keyframes come from the same seeded generator as the prototype, so demo
/// screenshots can be compared with the design pixel for pixel.
/// </summary>
public sealed class DesignSample : IMediaPreview
{
    public const double SampleDuration = 872.48;

    private static readonly (double From, double To)[] Silence =
    [
        (0, 11.2), (46.2, 52.4), (112.3, 123.1), (205, 213.6), (404.4, 417.8),
        (470, 480.2), (540.1, 546.3), (603, 611.8), (700, 712.5), (829.4, 872.48),
    ];

    private static readonly double[] Scenes = [46.2, 118.6, 242.88, 404.0, 495.2, 603, 745, 830.2];

    private static readonly (double Hue, double Lightness)[] Tones =
    [
        (250, .36), (55, .44), (215, .30), (30, .40), (195, .46), (285, .32), (145, .38), (20, .42), (235, .34),
    ];

    private readonly double[][] _amps;
    private readonly double[] _keyframes;

    public DesignSample()
    {
        var rnd = new Mulberry32(11);
        bool InSilence(double t) => Silence.Any(z => t >= z.From && t <= z.To);

        // The prototype first draws 360 bars it no longer shows; keep the calls so the sequence matches.
        double env = 0.6;
        for (int i = 0; i < 360; i++)
        {
            double t = (i + 0.5) / 360 * SampleDuration;
            env = Math.Min(0.95, Math.Max(0.35, env + (rnd.Next() - 0.5) * 0.3));
            _ = InSilence(t) ? 0.04 + rnd.Next() * 0.05 : env * (0.45 + rnd.Next() * 0.55);
        }

        var mic = new double[1200];
        double env2 = 0.6;
        for (int i = 0; i < mic.Length; i++)
        {
            double t = (i + 0.5) / mic.Length * SampleDuration;
            env2 = Math.Min(0.95, Math.Max(0.35, env2 + (rnd.Next() - 0.5) * 0.18));
            mic[i] = InSilence(t) ? 0.04 + rnd.Next() * 0.05 : env2 * (0.4 + rnd.Next() * 0.6);
        }
        var system = mic.Select((a, i) => Math.Min(0.9, a * 0.35 + 0.1 + 0.12 * Math.Abs(Math.Sin(i * 0.031)))).ToArray();
        var music = mic.Select((_, i) => Math.Min(0.8, 0.3 + 0.18 * Math.Abs(Math.Sin(i * 0.011)) + 0.1 * Math.Abs(Math.Sin(i * 0.7)))).ToArray();
        _amps = [mic, system, music];

        var kf = new List<double>();
        for (double t = 0; t < SampleDuration; t += 1.5 + rnd.Next() * 3)
            kf.Add(t);
        _keyframes = [.. kf];
    }

    public static IReadOnlyList<(string Key, string Label)> AudioStreams { get; } =
        [("A1", "Mic"), ("A2", "System"), ("A3", "Music")];

    public double Duration => SampleDuration;
    public double FrameRate => 29.97;
    public IReadOnlyList<double> Keyframes => _keyframes;
    public int AudioStreamCount => _amps.Length;

    /// <summary>Samples in the prototype's amplitude arrays (1200 over the whole file).</summary>
    public int AmplitudeSamples => _amps[0].Length;

    public double AudioPeak(int stream, double startTime, double endTime)
    {
        var a = _amps[stream];
        int n = a.Length;
        int j0 = (int)Math.Floor(startTime / SampleDuration * n);
        int j1 = Math.Max(j0 + 1, (int)Math.Floor(endTime / SampleDuration * n));
        double m = 0;
        for (int j = Math.Max(0, j0); j < j1 && j < n; j++)
            m = Math.Max(m, a[j]);
        return m;
    }

    /// <summary>Peak over sample indexes [j0, j1), exactly as the prototype bins its bars.</summary>
    public double AudioPeakBySample(int stream, int j0, int j1)
    {
        var a = _amps[stream];
        double m = 0;
        for (int j = j0; j < j1 && j < a.Length; j++)
            m = Math.Max(m, a[j]);
        return m;
    }

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
    {
        double jitter = look == FrameLook.Player ? 0 : ((variant % 7) - 3) * 0.008;
        double dark = look == FrameLook.Player ? 0.12 : 0;
        var (c0, c1, c2) = ToneStops(time, dark, jitter);
        if (look == FrameLook.Excluded)
        {
            c0 = Oklch.Grayscale(c0, 0.8);
            c1 = Oklch.Grayscale(c1, 0.8);
            c2 = Oklch.Grayscale(c2, 0.8);
        }
        context.FillRectangle(CssLinearGradient(rect, 165, c0, c1, c2), rect);
    }

    /// <summary>The prototype's <c>tone()</c> gradient stops (165° at 0%, 62%, 100%).</summary>
    public static (Color, Color, Color) ToneStops(double time, double dark, double jitter)
    {
        int i = Scenes.Count(x => x <= time);
        var (hue, baseL) = Tones[i];
        double l = baseL - dark + jitter;
        return (Oklch.ToColor(l + 0.08, 0.035, hue),
                Oklch.ToColor(l - 0.05, 0.028, hue),
                Oklch.ToColor(l - 0.13, 0.02, hue));
    }

    /// <summary>CSS <c>linear-gradient(angle, c0 0%, c1 62%, c2 100%)</c> over <paramref name="rect"/>.</summary>
    public static LinearGradientBrush CssLinearGradient(Rect rect, double angleDegrees, Color c0, Color c1, Color c2)
    {
        double a = angleDegrees * Math.PI / 180;
        double dx = Math.Sin(a), dy = -Math.Cos(a);
        double len = Math.Abs(rect.Width * dx) + Math.Abs(rect.Height * dy);
        var center = rect.Center;
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(center.X - dx * len / 2, center.Y - dy * len / 2, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(center.X + dx * len / 2, center.Y + dy * len / 2, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(c0, 0),
                new GradientStop(c1, 0.62),
                new GradientStop(c2, 1),
            },
        };
    }

    /// <summary>mulberry32, as used by the prototype (JavaScript int32 arithmetic).</summary>
    private sealed class Mulberry32(uint seed)
    {
        private uint _seed = seed;

        public double Next()
        {
            unchecked
            {
                _seed += 0x6D2B79F5;
                uint t = (_seed ^ (_seed >> 15)) * (1 | _seed);
                t = (t + ((t ^ (t >> 7)) * (61 | t))) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        }
    }
}
