using Avalonia;
using Avalonia.Media;

namespace OurCut.App.Services;

/// <summary>How a frame is drawn: timeline thumbnail, greyed-out source strip, or the player.</summary>
public enum FrameLook
{
    Thumbnail,
    Excluded,
    Player,
}

/// <summary>
/// Everything the timeline and player need to draw a media file: frames, audio peaks and
/// keyframes. The demo implementation draws the design's placeholders; the real one draws
/// decoded thumbnails and waveforms.
/// </summary>
public interface IMediaPreview
{
    double Duration { get; }
    double FrameRate { get; }

    /// <summary>Keyframe times, sorted. Empty until the file has been scanned.</summary>
    IReadOnlyList<double> Keyframes { get; }

    int AudioStreamCount { get; }

    /// <summary>True for the design's shaded placeholder frames (demo mode).</summary>
    bool IsPlaceholder { get; }

    /// <summary>
    /// What is still being analysed, e.g. "analysing 45%"; null when everything is ready.
    /// </summary>
    string? Activity { get; }

    /// <summary>Why part of the analysis failed (e.g. a damaged audio stream); null if nothing did.</summary>
    string? AnalysisError { get; }

    /// <summary>Raised on the UI thread when more thumbnails, waveform or keyframes are available.</summary>
    event EventHandler? Changed;

    /// <summary>Peak level 0..1 of an audio stream between two source times.</summary>
    double AudioPeak(int stream, double startTime, double endTime);

    /// <summary>Draws the frame at <paramref name="time"/> into <paramref name="rect"/>.</summary>
    /// <param name="variant">Stable per-thumbnail number, used by the demo to vary placeholder shading.</param>
    void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant);
}
