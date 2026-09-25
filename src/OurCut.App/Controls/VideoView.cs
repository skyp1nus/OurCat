using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using OurCut.App.Services;
using OurCut.Media.Playback;

namespace OurCut.App.Controls;

/// <summary>
/// Shows the video of an <see cref="MpvPlaybackEngine"/>. It renders with OpenGL when the platform
/// offers it and falls back to mpv's software renderer otherwise (or when OpenGL fails to start).
/// Until the first frame arrives it draws nothing, so the thumbnail underneath stays visible.
/// </summary>
public sealed class VideoView : Decorator
{
    public static readonly StyledProperty<IPlayer?> PlayerProperty =
        AvaloniaProperty.Register<VideoView, IPlayer?>(nameof(Player));

    /// <summary>Settings → Playback → Renderer: Software. Changing it rebuilds the view while the video plays on.</summary>
    public static readonly StyledProperty<bool> SoftwareOnlyProperty =
        AvaloniaProperty.Register<VideoView, bool>(nameof(SoftwareOnly));

    /// <summary>How long OpenGL may take to start before the software renderer is used instead.</summary>
    private static readonly TimeSpan OpenGlStartTimeout = TimeSpan.FromSeconds(2);

    private DispatcherTimer? _fallbackTimer;

    public IPlayer? Player { get => GetValue(PlayerProperty); set => SetValue(PlayerProperty, value); }

    public bool SoftwareOnly { get => GetValue(SoftwareOnlyProperty); set => SetValue(SoftwareOnlyProperty, value); }

    /// <summary>
    /// Try OpenGL first. Off in headless tests; <c>OURCUT_VIDEO=software</c> turns it off as well.
    /// </summary>
    public static bool PreferOpenGl { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("OURCUT_VIDEO"), "software", StringComparison.OrdinalIgnoreCase);

    /// <summary>"opengl", "software" or null (no video), for diagnostics and tests.</summary>
    public string? Mode => Child switch
    {
        OpenGlVideoView { IsRunning: true } => "opengl",
        SoftwareVideoView => "software",
        _ => null,
    };

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PlayerProperty || change.Property == SoftwareOnlyProperty)
            Rebuild();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        StopFallbackTimer();
        Child = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void Rebuild()
    {
        StopFallbackTimer();
        Child = null;
        if (VisualRoot is null || Player is not MpvPlaybackEngine engine)
            return;
        if (!PreferOpenGl || SoftwareOnly)
        {
            Child = new SoftwareVideoView(engine.Mpv);
            return;
        }
        var gl = new OpenGlVideoView(engine.Mpv);
        gl.Failed += (_, _) => UseSoftware(engine);
        Child = gl;
        _fallbackTimer = new DispatcherTimer(OpenGlStartTimeout, DispatcherPriority.Background, (_, _) =>
        {
            // OpenGL starts when the view is first drawn; if it has not by now, the platform has none.
            if (gl.IsRunning || !IsEffectivelyVisible || Bounds.Width <= 0)
                return;
            UseSoftware(engine);
        });
        _fallbackTimer.Start();
    }

    private void UseSoftware(MpvPlaybackEngine engine)
    {
        StopFallbackTimer();
        if (Child is SoftwareVideoView || !ReferenceEquals(Player, engine))
            return;
        Child = null;
        Child = new SoftwareVideoView(engine.Mpv);
    }

    private void StopFallbackTimer()
    {
        _fallbackTimer?.Stop();
        _fallbackTimer = null;
    }
}

/// <summary>mpv's OpenGL renderer drawing straight into Avalonia's OpenGL surface.</summary>
[SuppressMessage("Reliability", "CA1001", Justification = "The renderer is released in OnOpenGlDeinit, with the GL context current.")]
internal sealed class OpenGlVideoView(MpvPlayer player) : OpenGlControlBase
{
    private MpvOpenGlRenderer? _renderer;

    public bool IsRunning => _renderer is not null;

    /// <summary>OpenGL started but mpv could not use it, or the context was lost.</summary>
    public event EventHandler? Failed;

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            _renderer = new MpvOpenGlRenderer(player, gl.GetProcAddress);
            _renderer.UpdateRequested += (_, _) => Dispatcher.UIThread.Post(RequestNextFrameRendering, DispatcherPriority.Render);
        }
        catch (Exception e) when (e is MpvException or InvalidOperationException)
        {
            _renderer = null;
            Dispatcher.UIThread.Post(() => Failed?.Invoke(this, EventArgs.Empty));
        }
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_renderer is null)
            return;
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)Math.Round(Bounds.Width * scale));
        int h = Math.Max(1, (int)Math.Round(Bounds.Height * scale));
        _renderer.HasNewFrame();
        _renderer.Render(fb, w, h, flipY: true);
        _renderer.ReportSwap();
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        _renderer?.Dispose();
        _renderer = null;
    }

    protected override void OnOpenGlLost()
    {
        // mpv allows one renderer per player, so release this one (its GL objects are gone with the
        // context anyway) before the software view takes over.
        _renderer?.Dispose();
        _renderer = null;
        Dispatcher.UIThread.Post(() => Failed?.Invoke(this, EventArgs.Empty));
    }
}

/// <summary>
/// mpv's software renderer. A background thread renders each new frame at the view's pixel size
/// into one of two buffers; the UI thread copies the newest into a bitmap and draws it.
/// </summary>
[SuppressMessage("Reliability", "CA1001", Justification = "Renderer, thread and buffers are released when the view is detached.")]
internal sealed class SoftwareVideoView : Control
{
    private const int MaxPixels = 3840 * 2160;

    private readonly MpvPlayer _player;
    private readonly Lock _frameLock = new();
    private readonly AutoResetEvent _wake = new(false);
    private MpvSoftwareRenderer? _renderer;
    private Thread? _thread;
    private volatile bool _stop;
    private int _targetWidth, _targetHeight, _resized, _framePosted;
    private FrameBuffer? _back, _front;
    private WriteableBitmap? _bitmap;

    public SoftwareVideoView(MpvPlayer player)
    {
        _player = player;
        ClipToBounds = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _stop = false;
        try
        {
            _renderer = new MpvSoftwareRenderer(_player);
        }
        catch (Exception ex) when (ex is MpvException or InvalidOperationException)
        {
            // No video (another renderer still holds the player); the thumbnail preview stays visible.
            System.Diagnostics.Debug.WriteLine("Software video renderer unavailable: " + ex.Message);
            return;
        }
        _renderer.UpdateRequested += (_, _) => _wake.Set();
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "mpv software render" };
        _thread.Start();
        UpdateTargetSize();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _stop = true;
        _wake.Set();
        _thread?.Join();
        _thread = null;
        _renderer?.Dispose();
        _renderer = null;
        lock (_frameLock)
        {
            _back?.Dispose();
            _front?.Dispose();
            _back = _front = null;
        }
        _bitmap?.Dispose();
        _bitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty)
            UpdateTargetSize();
    }

    private void UpdateTargetSize()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        int w = (int)Math.Round(Bounds.Width * scale), h = (int)Math.Round(Bounds.Height * scale);
        if (w * (long)h > MaxPixels)
        {
            double f = Math.Sqrt((double)MaxPixels / ((long)w * h));
            w = (int)(w * f);
            h = (int)(h * f);
        }
        Volatile.Write(ref _targetWidth, w);
        Volatile.Write(ref _targetHeight, h);
        Volatile.Write(ref _resized, 1);
        _wake.Set();
    }

    private void RenderLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            if (_stop)
                return;
            var renderer = _renderer;
            if (renderer is null)
                continue;
            bool newFrame = renderer.HasNewFrame();
            bool resized = Interlocked.Exchange(ref _resized, 0) == 1;
            int w = Volatile.Read(ref _targetWidth), h = Volatile.Read(ref _targetHeight);
            if ((!newFrame && !resized) || w <= 0 || h <= 0)
                continue;
            try
            {
                var back = _back is { } b && b.Width == w && b.Height == h ? b : new FrameBuffer(w, h);
                if (!ReferenceEquals(back, _back))
                {
                    _back?.Dispose();
                    _back = back;
                }
                renderer.Render(back.Pixels, w, h, back.Stride);
                lock (_frameLock)
                    (_front, _back) = (_back, _front);
            }
            catch (MpvException)
            {
                continue;
            }
            if (Interlocked.Exchange(ref _framePosted, 1) == 0)
                Dispatcher.UIThread.Post(PresentFrame, DispatcherPriority.Render);
        }
    }

    /// <summary>Copies the newest frame into the bitmap (UI thread).</summary>
    private void PresentFrame()
    {
        Volatile.Write(ref _framePosted, 0);
        lock (_frameLock)
        {
            if (_front is not { } frame)
                return;
            if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                    PixelFormat.Bgra8888, AlphaFormat.Opaque);
            }
            using var target = _bitmap.Lock();
            frame.CopyTo(target.Address, target.RowBytes);
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_bitmap is { } bitmap)
            context.DrawImage(bitmap, new Rect(bitmap.Size), new Rect(Bounds.Size));
    }

    /// <summary>A 64-byte aligned BGRX frame, as mpv's software renderer prefers.</summary>
    private sealed unsafe class FrameBuffer : IDisposable
    {
        public FrameBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Stride = (width * 4 + 63) / 64 * 64;
            Pixels = (IntPtr)NativeMemory.AlignedAlloc((nuint)(Stride * height), 64);
        }

        public int Width { get; }
        public int Height { get; }
        public int Stride { get; }
        public IntPtr Pixels { get; private set; }

        public void CopyTo(IntPtr target, int targetStride)
        {
            int row = Width * 4;
            for (int y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)Pixels + y * Stride, (byte*)target + y * targetStride, targetStride, row);
        }

        public void Dispose()
        {
            if (Pixels == IntPtr.Zero)
                return;
            NativeMemory.AlignedFree((void*)Pixels);
            Pixels = IntPtr.Zero;
        }
    }
}
