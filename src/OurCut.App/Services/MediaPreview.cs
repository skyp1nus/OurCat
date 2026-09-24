using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using OurCut.Media.Caching;
using OurCut.Media.Previews;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.App.Services;

/// <summary>
/// Preview of a real media file. Keyframes, the waveform and thumbnails are read from the cache or
/// extracted with ffmpeg/ffprobe in the background, all three at once, and appear on the timeline
/// as they arrive; the editor is usable immediately.
/// </summary>
public sealed class MediaPreview : IMediaPreview, IDisposable
{
    /// <summary>How much excluded thumbnails are desaturated, like CSS <c>grayscale(0.8)</c>.</summary>
    private const double ExcludedGrey = 0.8;

    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#1C1C1C"));

    private readonly CancellationTokenSource _cts = new();
    private readonly MediaCache? _cache;
    private readonly List<Thumbnail> _thumbnails = [];
    private readonly Lock _lock = new();
    private readonly TaskCompletionSource<IReadOnlyList<double>> _keyframesReady =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly int _expectedThumbnails;
    private double[] _keyframes = [];
    private double _keyframeProgress;
    private volatile bool _thumbnailsDone;
    private volatile bool _analysing;
    private volatile string? _error;
    private int _changePending;
    private bool _disposed;

    private sealed record Thumbnail(double Time, Bitmap Color, Bitmap Grey);

    public MediaPreview(MediaInfo info, MediaCache? cache)
    {
        Info = info;
        _cache = cache;
        Waveform = WaveformExtractor.Create(info);
        _expectedThumbnails = info.Video is null ? 0 : (int)Math.Floor(info.Duration / ThumbnailExtractor.IntervalFor(info.Duration)) + 1;
        if (info.Video is null)
        {
            _keyframeProgress = 1;
            _thumbnailsDone = true;
        }
    }

    public MediaInfo Info { get; }
    public WaveformData Waveform { get; }
    public double Duration => Info.Duration;
    public double FrameRate => Info.Video?.FrameRate ?? 0;
    public IReadOnlyList<double> Keyframes => Volatile.Read(ref _keyframes);
    public int AudioStreamCount => Info.Audio.Length;
    public bool IsPlaceholder => false;

    /// <summary>Completes with the keyframes once they are scanned (empty if the scan failed).</summary>
    public Task<IReadOnlyList<double>> KeyframesTask => _keyframesReady.Task;

    /// <summary>Completes when the background analysis has finished, failed or been cancelled.</summary>
    public Task Analysis { get; private set; } = Task.CompletedTask;

    /// <summary>Why part of the analysis failed, if it did.</summary>
    public string? AnalysisError => _error;

    public int ThumbnailCount
    {
        get
        {
            lock (_lock)
                return _thumbnails.Count;
        }
    }

    /// <summary>Analysis progress 0..1 (keyframes, waveform and thumbnails weigh the same).</summary>
    public double Progress
    {
        get
        {
            var parts = new List<double>(3);
            if (Info.Video is not null)
            {
                parts.Add(Volatile.Read(ref _keyframeProgress));
                parts.Add(_thumbnailsDone ? 1 : Math.Min(1, (double)ThumbnailCount / Math.Max(1, _expectedThumbnails)));
            }
            if (Info.Audio.Length > 0)
                parts.Add(Waveform.IsComplete ? 1 : (double)Waveform.Filled / Math.Max(1, Waveform.Capacity));
            return parts.Count == 0 ? 1 : parts.Average();
        }
    }

    public string? Activity => _analysing ? $"analysing {Math.Floor(Progress * 100):0}%" : null;

    public event EventHandler? Changed;

    /// <summary>Starts the background analysis.</summary>
    public void Start()
    {
        if (_analysing || _disposed)
            return;
        _analysing = true;
        var ct = _cts.Token;
        Analysis = Task.Run(() => AnalyseAsync(ct), CancellationToken.None);
    }

    private async Task AnalyseAsync(CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(
                Guard(() => ScanKeyframesAsync(ct)),
                Guard(() => ExtractWaveformAsync(ct)),
                Guard(() => ExtractThumbnailsAsync(ct))).ConfigureAwait(false);
        }
        finally
        {
            _keyframesReady.TrySetResult(Keyframes);
            _analysing = false;
            NotifyChanged();
        }
    }

    /// <summary>One failing part (say a broken audio stream) does not stop the others.</summary>
    private async Task Guard(Func<Task> part)
    {
        try
        {
            await part().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is MediaToolException or IOException or UnauthorizedAccessException)
        {
            _error ??= e.Message;
        }
    }

    private async Task ScanKeyframesAsync(CancellationToken ct)
    {
        if (Info.Video is null)
            return;
        double[]? keyframes = _cache?.LoadKeyframes(Info.Path);
        if (keyframes is null)
        {
            keyframes = await KeyframeScanner.ScanAsync(Info, new Reporter<double>(f =>
            {
                Volatile.Write(ref _keyframeProgress, f);
                NotifyChanged();
            }), ct).ConfigureAwait(false);
            _cache?.SaveKeyframes(Info.Path, keyframes);
        }
        Volatile.Write(ref _keyframes, keyframes);
        Volatile.Write(ref _keyframeProgress, 1);
        _keyframesReady.TrySetResult(keyframes);
        NotifyChanged();
    }

    private async Task ExtractWaveformAsync(CancellationToken ct)
    {
        if (Info.Audio.Length == 0)
            return;
        if (_cache?.LoadWaveform(Info.Path) is { } cached && cached.StreamCount == Info.Audio.Length)
        {
            Waveform.CopyFrom(cached);
            NotifyChanged();
            return;
        }
        await WaveformExtractor.ExtractAsync(Info, Waveform, NotifyChanged, ct).ConfigureAwait(false);
        _cache?.SaveWaveform(Info.Path, Waveform);
    }

    private async Task ExtractThumbnailsAsync(CancellationToken ct)
    {
        if (Info.Video is null)
            return;
        try
        {
            int height = ThumbnailExtractor.DefaultHeight;
            if (_cache?.LoadThumbnails(Info.Path, height) is { Count: > 0 } cached)
            {
                foreach (var frame in cached)
                    Add(frame);
                return;
            }
            var frames = new List<ThumbnailFrame>();
            await ThumbnailExtractor.ExtractAsync(Info, frame =>
            {
                frames.Add(frame);
                Add(frame);
            }, height, cancellationToken: ct).ConfigureAwait(false);
            _cache?.SaveThumbnails(Info.Path, frames);
        }
        finally
        {
            _thumbnailsDone = true;
            NotifyChanged();
        }
    }

    private void Add(ThumbnailFrame frame)
    {
        var color = ToBitmap(frame.Bgra, frame.Width, frame.Height);
        var grey = ToBitmap(Desaturate(frame.Bgra, ExcludedGrey), frame.Width, frame.Height);
        lock (_lock)
        {
            if (_disposed)
            {
                color.Dispose();
                grey.Dispose();
                return;
            }
            _thumbnails.Add(new Thumbnail(frame.Time, color, grey));
        }
        NotifyChanged();
    }

    private static Bitmap ToBitmap(byte[] bgra, int width, int height)
    {
        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, handle.AddrOfPinnedObject(),
                new PixelSize(width, height), new Vector(96, 96), width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Moves each pixel towards its luminance (Rec. 709 weights, as CSS grayscale()).</summary>
    internal static byte[] Desaturate(byte[] bgra, double amount)
    {
        var result = new byte[bgra.Length];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i], g = bgra[i + 1], r = bgra[i + 2];
            double y = 0.2126 * r + 0.7152 * g + 0.0722 * b;
            result[i] = (byte)Math.Round(b + (y - b) * amount);
            result[i + 1] = (byte)Math.Round(g + (y - g) * amount);
            result[i + 2] = (byte)Math.Round(r + (y - r) * amount);
            result[i + 3] = 255;
        }
        return result;
    }

    /// <summary>
    /// Raises <see cref="Changed"/> on the UI thread, at most about ten times a second however fast
    /// thumbnails arrive.
    /// </summary>
    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref _changePending, 1) == 1)
            return;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await Task.Delay(100).ConfigureAwait(true);
            Volatile.Write(ref _changePending, 0);
            if (!_disposed)
                Changed?.Invoke(this, EventArgs.Empty);
        }, DispatcherPriority.Background);
    }

    // ---- Drawing -------------------------------------------------------------------------

    public double AudioPeak(int stream, double startTime, double endTime) => Waveform.Peak(stream, startTime, endTime);

    public void DrawFrame(DrawingContext context, Rect rect, double time, FrameLook look, int variant)
    {
        Bitmap? bitmap;
        lock (_lock)
        {
            var thumb = Nearest(time);
            bitmap = thumb is null ? null : look == FrameLook.Excluded ? thumb.Grey : thumb.Color;
            if (bitmap is null)
            {
                context.FillRectangle(look == FrameLook.Player ? Brushes.Black : PendingBrush, rect);
                return;
            }
            var size = new Size(bitmap.PixelSize.Width, bitmap.PixelSize.Height);
            if (look == FrameLook.Player)
            {
                // Until playback arrives the player shows the nearest thumbnail, letterboxed.
                context.FillRectangle(Brushes.Black, rect);
                context.DrawImage(bitmap, new Rect(size), Fit(size, rect));
            }
            else
            {
                context.DrawImage(bitmap, Cover(size, rect.Size), rect);
            }
        }
    }

    /// <summary>The thumbnail closest in time. Call with the lock held.</summary>
    private Thumbnail? Nearest(double time)
    {
        if (_thumbnails.Count == 0)
            return null;
        int lo = 0, hi = _thumbnails.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_thumbnails[mid].Time < time)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo > 0 && time - _thumbnails[lo - 1].Time < _thumbnails[lo].Time - time)
            lo--;
        return _thumbnails[lo];
    }

    /// <summary>The part of an image that fills <paramref name="target"/> without distortion (centre crop).</summary>
    internal static Rect Cover(Size image, Size target)
    {
        if (target.Width <= 0 || target.Height <= 0)
            return new Rect(image);
        double scale = Math.Max(target.Width / image.Width, target.Height / image.Height);
        double w = target.Width / scale, h = target.Height / scale;
        return new Rect((image.Width - w) / 2, (image.Height - h) / 2, w, h);
    }

    /// <summary>Where an image goes to fit entirely inside <paramref name="target"/>, centred.</summary>
    internal static Rect Fit(Size image, Rect target)
    {
        double scale = Math.Min(target.Width / image.Width, target.Height / image.Height);
        double w = image.Width * scale, h = image.Height * scale;
        return new Rect(target.X + (target.Width - w) / 2, target.Y + (target.Height - h) / 2, w, h);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var t in _thumbnails)
            {
                t.Color.Dispose();
                t.Grey.Dispose();
            }
            _thumbnails.Clear();
        }
        _cts.Cancel();
        // The analysis still holds the token until it notices the cancellation.
        Analysis.ContinueWith(_ => _cts.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>An <see cref="IProgress{T}"/> that reports on the calling thread (no UI marshalling).</summary>
    private sealed class Reporter<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
