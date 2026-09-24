using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.Core.Time;

namespace OurCut.App.Controls;

/// <summary>
/// The timeline: ruler, the whole source file as a video lane plus one lane per audio stream,
/// kept clips on top of the greyed-out excluded parts, trim handles and the playhead.
/// Layout follows the design: ruler 26 px, content from y = 30, video lane 52 px, audio lanes 24 px.
/// </summary>
public sealed class TimelineControl : Control
{
    public static readonly StyledProperty<EditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<TimelineControl, EditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<double> ScrollOffsetProperty =
        AvaloniaProperty.Register<TimelineControl, double>(nameof(ScrollOffset));

    public static readonly DirectProperty<TimelineControl, double> ExtentWidthProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, double>(nameof(ExtentWidth), o => o.ExtentWidth);

    public static readonly DirectProperty<TimelineControl, double> MaxScrollProperty =
        AvaloniaProperty.RegisterDirect<TimelineControl, double>(nameof(MaxScroll), o => o.MaxScroll);

    private const double RulerHeight = 26;
    private const double ContentTop = 30;
    private const double ContentHeight = 134;
    private const double VideoHeight = 52;
    private const double AudioTop = 54;
    private const double AudioPitch = 26;
    private const double AudioHeight = 24;
    private const double HandleWidth = 9;

    private static readonly Color Accent = Color.Parse("#60CDFF");
    private static readonly Color Violet = Color.Parse("#AA9CF7");
    private static readonly IBrush RulerBg = new SolidColorBrush(Color.Parse("#222222"));
    private static readonly IBrush MinorTick = new SolidColorBrush(Color.Parse("#3C3C3C"));
    private static readonly IBrush MidTick = new SolidColorBrush(Color.Parse("#5A5A5A"));
    private static readonly IBrush MajorTick = new SolidColorBrush(Color.Parse("#6A6A6A"));
    private static readonly IBrush RulerText = new SolidColorBrush(Color.Parse("#C8C8C8"));
    private static readonly IBrush LaneBg = new SolidColorBrush(Color.Parse("#151515"));
    private static readonly IBrush BaseThumbBorder = new SolidColorBrush(Color.Parse("#121212"));
    private static readonly IBrush BaseBar = new SolidColorBrush(Color.Parse("#353535"));
    private static readonly IBrush HatchBrush = new SolidColorBrush(Color.FromArgb(13, 255, 255, 255));
    private static readonly IBrush GapText = new SolidColorBrush(Color.Parse("#8A8A8A"));
    private static readonly IBrush ClipBg = new SolidColorBrush(Color.Parse("#262626"));
    private static readonly IBrush ClipThumbBorder = new SolidColorBrush(Color.Parse("#141414"));
    private static readonly IBrush ClipAudioBg = new SolidColorBrush(Color.Parse("#1A262B"));
    private static readonly IBrush ClipBar = new SolidColorBrush(Color.Parse("#7FA9BA"));
    private static readonly IBrush KeepBg = new SolidColorBrush(Color.Parse("#232323"));
    private static readonly IBrush KeepBgHover = new SolidColorBrush(Color.Parse("#333333"));
    private static readonly IBrush KeepBorder = new SolidColorBrush(Color.Parse("#4C4C4C"));
    private static readonly IBrush KeepText = new SolidColorBrush(Color.Parse("#E2E2E2"));
    private static readonly IBrush GripLine = new SolidColorBrush(Color.FromArgb(115, 0, 0, 0));
    private static readonly IBrush PlayheadBrush = Brushes.White;
    private static readonly IBrush PlayheadText = new SolidColorBrush(Color.Parse("#111111"));
    private static readonly IBrush EmptyBorder = new SolidColorBrush(Color.Parse("#404040"));

    private readonly List<HitRegion> _hits = [];
    private EditorViewModel? _editor;
    private double _extentWidth;
    private double _maxScroll;
    private Drag _drag;
    private HitRegion? _hover;
    private DispatcherTimer? _scanTimer;
    private double _scanPhase;

    static TimelineControl()
    {
        AffectsRender<TimelineControl>(ScrollOffsetProperty);
        ClipToBoundsProperty.OverrideDefaultValue<TimelineControl>(true);
        FocusableProperty.OverrideDefaultValue<TimelineControl>(false);
    }

    public EditorViewModel? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public double ScrollOffset { get => GetValue(ScrollOffsetProperty); set => SetValue(ScrollOffsetProperty, value); }
    public double ExtentWidth { get => _extentWidth; private set => SetAndRaise(ExtentWidthProperty, ref _extentWidth, value); }
    public double MaxScroll { get => _maxScroll; private set => SetAndRaise(MaxScrollProperty, ref _maxScroll, value); }

    /// <summary>Largest zoom factor: one frame becomes 12 px wide.</summary>
    public double MaxZoom
    {
        get
        {
            var e = _editor;
            if (e is null || e.Duration <= 0 || Bounds.Width <= 0)
                return 6;
            return Math.Max(6, e.Duration * e.FrameRate * 12 / Bounds.Width);
        }
    }

    /// <summary>Zoom factor from the slider position: 1× (whole file) up to frame level, exponential.</summary>
    public double Zoom => Math.Pow(MaxZoom, Math.Clamp(_editor?.ZoomLevel ?? 0, 0, 1));

    private double Duration => _editor?.Duration ?? 0;
    private double Inner => Math.Max(Bounds.Width, Bounds.Width * Zoom);
    private double Pps => Duration > 0 ? Inner / Duration : 0;
    private double X(double t) => Duration > 0 ? t / Duration * Inner : 0;
    private double T(double x) => Inner > 0 ? x / Inner * Duration : 0;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            if (_editor is not null)
            {
                _editor.TimelineChanged -= OnEditorChanged;
                _editor.PropertyChanged -= OnEditorPropertyChanged;
            }
            _editor = change.GetNewValue<EditorViewModel?>();
            if (_editor is not null)
            {
                _editor.TimelineChanged += OnEditorChanged;
                _editor.PropertyChanged += OnEditorPropertyChanged;
            }
            UpdateExtent();
            InvalidateVisual();
        }
        else if (change.Property == BoundsProperty)
        {
            UpdateExtent();
        }
    }

    private double _lastZoom = 1;

    private void OnEditorPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.ZoomLevel))
        {
            // Keep the playhead where it is on screen while zooming.
            double oldInner = Math.Max(Bounds.Width, Bounds.Width * _lastZoom);
            double anchor = Duration > 0 ? _editor!.Time / Duration * oldInner - ScrollOffset : 0;
            UpdateExtent();
            ScrollOffset = Math.Clamp(X(_editor!.Time) - anchor, 0, MaxScroll);
        }
        else if (e.PropertyName is nameof(EditorViewModel.Media) or nameof(EditorViewModel.IsMediaLoaded)
                 or nameof(EditorViewModel.PlaceholderDuration))
        {
            UpdateExtent();
            InvalidateVisual();
        }
        else if (e.PropertyName == nameof(EditorViewModel.Time) && _editor is { IsPlaying: true } && _drag.Kind == DragKind.None)
        {
            FollowPlayhead();
        }
    }

    private void OnEditorChanged(object? sender, EventArgs e)
    {
        UpdateScanTimer();
        InvalidateVisual();
    }

    private void UpdateExtent()
    {
        _lastZoom = Zoom;
        ExtentWidth = Inner;
        MaxScroll = Math.Max(0, Inner - Bounds.Width);
        if (ScrollOffset > MaxScroll)
            ScrollOffset = MaxScroll;
        InvalidateVisual();
    }

    private void FollowPlayhead()
    {
        double x = X(_editor!.Time);
        if (x < ScrollOffset || x > ScrollOffset + Bounds.Width - 40)
            ScrollOffset = Math.Clamp(x - Bounds.Width * 0.1, 0, MaxScroll);
    }

    /// <summary>Animates the violet sweep over clips Claude is working on.</summary>
    private void UpdateScanTimer()
    {
        bool working = _editor?.Clips.Any(c => c.IsAiWorking) ?? false;
        if (working && _scanTimer is null)
        {
            var start = DateTime.UtcNow;
            _scanTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) =>
            {
                _scanPhase = (DateTime.UtcNow - start).TotalSeconds / 1.6 % 1;
                InvalidateVisual();
            });
            _scanTimer.Start();
        }
        else if (!working && _scanTimer is not null)
        {
            _scanTimer.Stop();
            _scanTimer = null;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _scanTimer?.Stop();
        _scanTimer = null;
        base.OnDetachedFromVisualTree(e);
    }

    // ---- Rendering -----------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        _hits.Clear();
        var editor = _editor;
        if (editor is null || Bounds.Width <= 0)
            return;
        UpdateScanTimer();

        double scroll = Math.Clamp(ScrollOffset, 0, MaxScroll);
        var visible = new Rect(scroll, 0, Bounds.Width, Bounds.Height);
        using var _ = context.PushTransform(Matrix.CreateTranslation(-scroll, 0));

        DrawRuler(context, editor, visible);
        if (!editor.HasFile)
        {
            DrawEmpty(context, visible);
            return;
        }

        var media = editor.Media!;
        DrawSourceBase(context, editor, media, visible);
        DrawGaps(context, editor, visible);

        var ordered = editor.Clips
            .Select((c, i) => (Clip: c, Index: i))
            .OrderBy(x => x.Clip.IsSelected ? 4 : x.Clip.IsIncluded ? 3 : 2)
            .ThenBy(x => x.Index);
        foreach (var (clip, _) in ordered)
            DrawClip(context, editor, media, clip, visible);

        DrawPlayhead(context, editor);
    }

    private void DrawRuler(DrawingContext ctx, EditorViewModel editor, Rect visible)
    {
        double w = Inner;
        var rulerRect = new Rect(0, 0, w, RulerHeight);
        using var clip = ctx.PushClip(new RoundedRect(rulerRect, 4));
        ctx.FillRectangle(RulerBg, rulerRect);
        if (Duration <= 0)
            return;

        double pps = Pps;
        // Labelled ticks at least 84 px apart (the design uses 5 s … 2 min at 1×).
        double iv = RulerSteps.FirstOrDefault(c => c * pps >= 84);
        if (iv == 0)
            iv = RulerSteps[^1];

        DrawTicks(ctx, iv / 10 / Duration * w, 4, MinorTick, visible);
        DrawTicks(ctx, iv / 2 / Duration * w, 8, MidTick, visible);

        var font = MonoFace(FontWeight.Medium);
        for (int k = 0; ; k++)
        {
            double t = k * iv;
            if (t > Duration + 1e-9)
                break;
            double x = X(t);
            if (x > visible.Right + 60)
                break;
            if (x < visible.Left - 120)
                continue;
            ctx.FillRectangle(MajorTick, new Rect(Math.Floor(x), 0, 1, RulerHeight));
            // The playhead label covers ruler labels next to it.
            if (Math.Abs(t - editor.Time) * pps >= 58)
            {
                var text = Text(RulerLabel(t, iv), font, 10.5, RulerText);
                ctx.DrawText(text, new Point(Math.Floor(x) + 6, 4 + (14.7 - text.Height) / 2));
            }
        }

        foreach (var clipVm in editor.Clips)
        {
            if (!clipVm.IsIncluded)
                continue;
            var color = clipVm.IsAi ? Violet : clipVm.IsSelected ? Accent : Color.FromArgb(140, Accent.R, Accent.G, Accent.B);
            ctx.FillRectangle(new SolidColorBrush(color), new Rect(X(clipVm.Start), RulerHeight - 2, X(clipVm.End) - X(clipVm.Start), 2));
        }
    }

    private static readonly double[] RulerSteps =
        [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];

    private static string RulerLabel(double t, double interval)
    {
        if (interval < 1)
        {
            long tenths = (long)Math.Round(t * 10);
            long s = tenths / 10;
            return string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}.{tenths % 10}");
        }
        long whole = (long)Math.Floor(t + 1e-6);
        return whole >= 3600
            ? string.Create(CultureInfo.InvariantCulture, $"{whole / 3600}:{whole / 60 % 60:00}:{whole % 60:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{whole / 60}:{whole % 60:00}");
    }

    private static void DrawTicks(DrawingContext ctx, double step, double height, IBrush brush, Rect visible)
    {
        if (step < 2)
            return;
        double start = Math.Floor(visible.Left / step) * step;
        for (double x = start; x <= visible.Right; x += step)
            ctx.FillRectangle(brush, new Rect(Math.Floor(x), RulerHeight - height, 1, height));
    }

    private void DrawEmpty(DrawingContext ctx, Rect visible)
    {
        var rect = new Rect(visible.Left + 0.75, ContentTop + 0.75, Bounds.Width - 1.5, ContentHeight - 1.5);
        var pen = new Pen(EmptyBorder, 1.5, new DashStyle([3, 2], 0));
        ctx.DrawRectangle(null, pen, new RoundedRect(rect, 6));
        var text = Text("Open a video. The whole file appears here and you mark what to keep.", SansFace(FontWeight.Normal), 12, GapText);
        ctx.DrawText(text, new Point(rect.Center.X - text.Width / 2, rect.Center.Y - text.Height / 2));
    }

    /// <summary>The whole source, greyed out: excluded parts show this through.</summary>
    private void DrawSourceBase(DrawingContext ctx, EditorViewModel editor, IMediaPreview media, Rect visible)
    {
        double w = Inner;
        var video = new Rect(0, ContentTop, w, VideoHeight);
        using (ctx.PushClip(new RoundedRect(video, 4)))
        {
            ctx.FillRectangle(LaneBg, video);
            int n = (int)Math.Round(12 * Zoom);
            double tw = w / n;
            using (ctx.PushOpacity(0.22))
            {
                for (int i = (int)Math.Max(0, Math.Floor(visible.Left / tw)); i < n && i * tw < visible.Right; i++)
                {
                    var r = new Rect(i * tw, ContentTop, tw, VideoHeight);
                    media.DrawFrame(ctx, r, (i + 0.5) / n * Duration, FrameLook.Excluded, i * 37);
                    ctx.FillRectangle(BaseThumbBorder, new Rect(r.Right - 1, r.Y, 1, r.Height));
                }
            }
        }

        for (int si = 0; si < editor.AudioLanes.Count; si++)
        {
            var lane = new Rect(0, ContentTop + AudioTop + si * AudioPitch, w, AudioHeight);
            using (ctx.PushOpacity(editor.AudioLanes[si].IsMuted ? 0.3 : 1))
            using (ctx.PushClip(new RoundedRect(lane, 4)))
            {
                ctx.FillRectangle(LaneBg, lane);
                int count = Math.Max(360, (int)Math.Floor((w - 4) / 4));
                DrawBars(ctx, media, si, lane.Deflate(new Thickness(2, 0)), 0, Duration, count, BaseBar, visible);
            }
        }

        Hatch.Draw(ctx, new Rect(0, ContentTop, w, ContentHeight), visible, HatchBrush, 7);
    }

    /// <summary>Waveform bars spread over <paramref name="rect"/> with 1 px gaps, centred vertically.</summary>
    private void DrawBars(DrawingContext ctx, IMediaPreview media, int stream, Rect rect, double from, double to, int count,
        IBrush brush, Rect visible)
    {
        if (count <= 0 || rect.Width <= 0)
            return;
        double bw = (rect.Width - (count - 1)) / count;
        if (bw <= 0)
            return;
        var sample = media as DesignSample;
        int n = sample?.AmplitudeSamples ?? 0;
        int a0 = 0, a1 = 0;
        if (sample is not null)
        {
            a0 = (int)Math.Floor(from / Duration * n);
            a1 = Math.Max(a0 + 1, (int)Math.Ceiling(to / Duration * n));
        }
        int first = (int)Math.Max(0, Math.Floor((visible.Left - rect.X) / (bw + 1)) - 1);
        for (int k = first; k < count; k++)
        {
            double x = rect.X + k * (bw + 1);
            if (x > visible.Right)
                break;
            double peak;
            if (sample is not null && from == 0 && to >= Duration && count == 360)
            {
                // The prototype's full-lane binning.
                peak = sample.AudioPeakBySample(stream, (int)Math.Floor((double)k * n / count), (int)Math.Floor((double)(k + 1) * n / count));
            }
            else if (sample is not null && !(from == 0 && to >= Duration))
            {
                int j0 = a0 + (int)Math.Floor((double)k * (a1 - a0) / count);
                int j1 = Math.Max(j0 + 1, a0 + (int)Math.Floor((double)(k + 1) * (a1 - a0) / count));
                peak = sample.AudioPeakBySample(stream, j0, j1);
            }
            else
            {
                double t0 = from + (to - from) * k / count, t1 = from + (to - from) * (k + 1) / count;
                peak = media.AudioPeak(stream, t0, t1);
            }
            double h = Math.Round(peak * 100, 1) / 100 * rect.Height;
            if (h <= 0)
                continue;
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(x, rect.Y + (rect.Height - h) / 2, bw, h), Math.Min(1, bw / 2)));
        }
    }

    private void DrawGaps(DrawingContext ctx, EditorViewModel editor, Rect visible)
    {
        foreach (var (from, to) in editor.ExcludedGaps())
        {
            double x = X(from), w = X(to) - x;
            if (x > visible.Right || x + w < visible.Left)
                continue;
            var rect = new Rect(x, ContentTop, w, VideoHeight);
            using var clip = ctx.PushClip(rect);
            double cx = x + 8;
            if (w > 150)
            {
                var label = Text("Excluded", SansFace(FontWeight.Normal), 11, GapText);
                ctx.DrawText(label, new Point(cx, rect.Center.Y - label.Height / 2));
                cx += label.Width + 8;
            }
            if (w > 44)
            {
                var dur = Text(TimeFormat.WholeSeconds(to - from), MonoFace(FontWeight.Normal), 10.5, GapText);
                ctx.DrawText(dur, new Point(cx, rect.Center.Y - dur.Height / 2));
                cx += dur.Width + 8;
            }
            if (w > 150)
                DrawKeepButton(ctx, new Point(cx, rect.Center.Y - 9), new HitRegion(HitKind.KeepGap, null, from, to));
        }
    }

    private void DrawKeepButton(DrawingContext ctx, Point topLeft, HitRegion region)
    {
        var text = Text("+ Keep", SansFace(FontWeight.Medium), 10.5, KeepText);
        var rect = new Rect(topLeft.X, topLeft.Y, Math.Ceiling(text.Width) + 16, 18);
        bool hover = _hover is { } h && h.Kind == region.Kind && h.Clip == region.Clip && h.From == region.From;
        ctx.DrawRectangle(hover ? KeepBgHover : KeepBg, new Pen(KeepBorder, 1), new RoundedRect(rect.Deflate(0.5), 3));
        ctx.DrawText(hover ? Text("+ Keep", SansFace(FontWeight.Medium), 10.5, Brushes.White) : text,
            new Point(rect.X + 8, rect.Y + (18 - text.Height) / 2));
        _hits.Add(region with { Rect = rect });
    }

    private void DrawClip(DrawingContext ctx, EditorViewModel editor, IMediaPreview media, ClipViewModel clip, Rect visible)
    {
        double x = X(clip.Start), w = X(clip.End) - x;
        if (x > visible.Right + 20 || x + w < visible.Left - 20)
            return;
        var outer = new Rect(x + 1, ContentTop, Math.Max(0, w - 2), ContentHeight);
        bool ai = clip.IsAi, sel = clip.IsSelected;
        double pad = sel ? 14 : 7;

        if (clip.IsIncluded)
        {
            // Video block: header + thumbnails.
            var video = new Rect(outer.X, ContentTop, outer.Width, VideoHeight);
            using (ctx.PushClip(new RoundedRect(video, 4)))
            {
                ctx.FillRectangle(ClipBg, video);
                int nT = Math.Max(1, (int)Math.Round(w / 90));
                double tw = video.Width / nT;
                for (int k = 0; k < nT; k++)
                {
                    var r = new Rect(video.X + k * tw, ContentTop + 18, tw, VideoHeight - 18);
                    if (r.Right < visible.Left || r.X > visible.Right)
                        continue;
                    media.DrawFrame(ctx, r, clip.Start + (k + 0.5) / nT * clip.Duration, FrameLook.Thumbnail, k * 37 + clip.Id * 13);
                    ctx.FillRectangle(ClipThumbBorder, new Rect(r.Right - 1, r.Y, 1, r.Height));
                }

                var headerBg = ai ? Violet : sel ? Accent : Color.Parse("#34474F");
                var headerFg = new SolidColorBrush(ai ? Color.Parse("#140F22") : sel ? Colors.Black : Color.Parse("#DFF4FD"));
                var header = new Rect(video.X, ContentTop, video.Width, 18);
                ctx.FillRectangle(new SolidColorBrush(headerBg), header);
                DrawClipHeader(ctx, clip, header, pad, headerFg, w > 120, visible);
            }

            // Audio blocks.
            for (int si = 0; si < editor.AudioLanes.Count; si++)
            {
                var lane = new Rect(outer.X, ContentTop + AudioTop + si * AudioPitch, outer.Width, AudioHeight);
                using (ctx.PushOpacity(editor.AudioLanes[si].IsMuted ? 0.3 : 1))
                using (ctx.PushClip(new RoundedRect(lane, 4)))
                {
                    ctx.FillRectangle(ClipAudioBg, lane);
                    int nb = Math.Max(3, (int)Math.Floor(w / 4));
                    DrawBars(ctx, media, si, lane.Deflate(new Thickness(2, 0)), clip.Start, clip.End, nb, ClipBar, visible);
                }
            }
        }
        else
        {
            // Excluded clip: dashed outline with a struck-through label.
            var pen = new Pen(new SolidColorBrush(Color.Parse("#6A6A6A")), 1, new DashStyle([3, 3], 0));
            ctx.DrawRectangle(null, pen, new RoundedRect(outer.Deflate(0.5), 4));
            using (ctx.PushClip(outer.Deflate(new Thickness(pad, 0))))
            {
                var num = Text(clip.Number.ToString(CultureInfo.InvariantCulture), MonoFace(FontWeight.SemiBold), 10.5, GapText);
                double lx = outer.X + pad;
                ctx.DrawText(num, new Point(lx, ContentTop + 4 + (14.7 - num.Height) / 2));
                lx += num.Width + 6;
                double keepSpace = w > 110 ? 60 : 0;
                var label = Text(clip.Label, SansFace(FontWeight.Medium), 10.5, GapText, Math.Max(0, outer.Right - pad - keepSpace - lx));
                var origin = new Point(lx, ContentTop + 4 + (14.7 - label.Height) / 2);
                ctx.DrawText(label, origin);
                // line-through at the font's strikeout position (0.319 em above the baseline).
                double sy = Math.Round(origin.Y + label.Baseline - 0.319 * 10.5);
                ctx.FillRectangle(GapText, new Rect(lx, sy, label.WidthIncludingTrailingWhitespace, 1));
            }
            if (w > 110)
            {
                var keepText = Text("+ Keep", SansFace(FontWeight.Medium), 10.5, KeepText);
                double bw = Math.Ceiling(keepText.Width) + 16;
                DrawKeepButton(ctx, new Point(outer.Right - pad - bw, ContentTop + 4), new HitRegion(HitKind.KeepClip, clip, 0, 0));
            }
        }

        if (clip.IsAiWorking)
        {
            using var clipScan = ctx.PushClip(new Rect(outer.X, ContentTop + 18, outer.Width, ContentHeight - 18));
            double sw = w * 0.3;
            double sx = x - sw + _scanPhase * 4.4 * sw;
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Colors.Transparent, 0),
                    new GradientStop(Color.FromArgb(102, Violet.R, Violet.G, Violet.B), 0.5),
                    new GradientStop(Colors.Transparent, 1),
                },
            };
            ctx.FillRectangle(brush, new Rect(sx, ContentTop + 18, sw, ContentHeight - 18));
        }

        // Ring.
        Color? ring = !clip.IsIncluded
            ? (sel ? Color.Parse("#A6A6A6") : null)
            : ai ? Violet : sel ? Accent : Color.FromArgb(26, 255, 255, 255);
        if (ring is { } rc && outer.Width > 0)
        {
            double rw = sel || ai ? 2 : 1;
            ctx.DrawRectangle(null, new Pen(new SolidColorBrush(rc), rw), new RoundedRect(outer.Deflate(rw / 2), 4 - rw / 2));
        }

        if (sel)
        {
            var handleColor = new SolidColorBrush(!clip.IsIncluded ? Color.Parse("#A6A6A6") : ai ? Violet : Accent);
            var left = new Rect(outer.X, ContentTop, HandleWidth, ContentHeight);
            var right = new Rect(outer.Right - HandleWidth, ContentTop, HandleWidth, ContentHeight);
            ctx.DrawRectangle(handleColor, null, new RoundedRect(left, 4, 0, 0, 4));
            ctx.DrawRectangle(handleColor, null, new RoundedRect(right, 0, 4, 4, 0));
            foreach (var h in new[] { left, right })
            {
                double gy = ContentTop + ContentHeight / 2 - 8;
                ctx.FillRectangle(GripLine, new Rect(h.X + 2.5, gy, 1, 16));
                ctx.FillRectangle(GripLine, new Rect(h.X + 5.5, gy, 1, 16));
            }
            _hits.Add(new HitRegion(HitKind.TrimIn, clip, 0, 0) { Rect = left });
            _hits.Add(new HitRegion(HitKind.TrimOut, clip, 0, 0) { Rect = right });
        }
    }

    private static void DrawClipHeader(DrawingContext ctx, ClipViewModel clip, Rect header, double pad, IBrush fg, bool showDuration,
        Rect visible)
    {
        using var clipHeader = ctx.PushClip(header);
        // Keep the name readable when the clip starts left of the view.
        double lx = Math.Max(header.X, Math.Min(visible.Left, header.Right - 80)) + pad, right = header.Right - pad;
        using (ctx.PushOpacity(0.75))
        {
            var num = Text(clip.Number.ToString(CultureInfo.InvariantCulture), MonoFace(FontWeight.SemiBold), 10, fg);
            ctx.DrawText(num, new Point(lx, header.Y + (18 - num.Height) / 2));
            lx += num.Width + 6;
        }
        FormattedText? dur = null;
        if (showDuration)
        {
            dur = Text(TimeFormat.WholeSeconds(clip.Duration), MonoFace(FontWeight.Medium), 10, fg);
            right -= dur.Width + 6;
        }
        var label = Text(clip.Label, SansFace(FontWeight.Medium), 11, fg, Math.Max(0, right - lx));
        ctx.DrawText(label, new Point(lx, header.Y + (18 - label.Height) / 2));
        if (dur is not null)
        {
            using (ctx.PushOpacity(0.8))
                ctx.DrawText(dur, new Point(header.Right - pad - dur.Width, header.Y + (18 - dur.Height) / 2));
        }
    }

    private void DrawPlayhead(DrawingContext ctx, EditorViewModel editor)
    {
        double x = X(editor.Time);
        ctx.FillRectangle(PlayheadBrush, new Rect(x - 0.5, 18, 1, Bounds.Height - 18));
        var text = Text(TimeFormat.MinutesSeconds(editor.Time), MonoFace(FontWeight.SemiBold), 10.5, PlayheadText);
        double lw = text.Width + 12;
        var rect = new Rect(x - lw / 2, 3, lw, 17);
        ctx.DrawRectangle(RulerBg, null, new RoundedRect(rect.Inflate(3), 6));
        ctx.DrawRectangle(PlayheadBrush, null, new RoundedRect(rect, 3));
        ctx.DrawText(text, new Point(rect.X + 6, rect.Y + (17 - text.Height) / 2));
    }

    // ---- Text helpers --------------------------------------------------------------------

    private static Typeface SansFace(FontWeight weight) => new(new FontFamily(FontSetup.Sans), FontStyle.Normal, weight);
    private static Typeface MonoFace(FontWeight weight) => new(new FontFamily(FontSetup.Mono), FontStyle.Normal, weight);

    private static FormattedText Text(string s, Typeface face, double size, IBrush brush, double maxWidth = double.PositiveInfinity)
    {
        var ft = new FormattedText(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, brush);
        if (!double.IsPositiveInfinity(maxWidth))
        {
            ft.MaxTextWidth = Math.Max(1, maxWidth);
            ft.MaxLineCount = 1;
            ft.Trimming = TextTrimming.CharacterEllipsis;
        }
        return ft;
    }

    // ---- Input ---------------------------------------------------------------------------

    private HitRegion? HitTest(Point p)
    {
        var content = new Point(p.X + ScrollOffset, p.Y);
        for (int i = _hits.Count - 1; i >= 0; i--)
        {
            var h = _hits[i];
            if (h.Rect.Inflate(new Thickness(h.Kind is HitKind.TrimIn or HitKind.TrimOut ? 2 : 0, 0)).Contains(content))
                return h;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var editor = _editor;
        if (editor is null || !editor.HasFile || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var p = e.GetPosition(this);
        var hit = HitTest(p);
        switch (hit?.Kind)
        {
            case HitKind.KeepGap:
                editor.KeepRange(hit.From, hit.To);
                break;
            case HitKind.KeepClip:
                editor.Keep(hit.Clip!);
                break;
            case HitKind.TrimIn or HitKind.TrimOut:
                _drag = new Drag(hit.Kind == HitKind.TrimIn ? DragKind.TrimIn : DragKind.TrimOut, hit.Clip,
                    p.X, hit.Kind == HitKind.TrimIn ? hit.Clip!.Start : hit.Clip!.End);
                editor.Select(hit.Clip);
                e.Pointer.Capture(this);
                break;
            default:
                _drag = new Drag(DragKind.Scrub, null, p.X, 0);
                editor.ScrubTo(T(p.X + ScrollOffset), select: true);
                e.Pointer.Capture(this);
                break;
        }
        e.Handled = true;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var editor = _editor;
        var p = e.GetPosition(this);
        if (editor is null)
            return;
        switch (_drag.Kind)
        {
            case DragKind.Scrub:
                editor.ScrubTo(T(p.X + ScrollOffset), select: false);
                return;
            case DragKind.TrimIn or DragKind.TrimOut:
                double v = _drag.Origin + (p.X - _drag.StartX) / Pps;
                v = editor.SnapToKeyframe(v, 8 / Pps);
                editor.Trim(_drag.Clip!, _drag.Kind == DragKind.TrimIn, v);
                return;
        }

        var hit = HitTest(p);
        Cursor = hit?.Kind is HitKind.TrimIn or HitKind.TrimOut ? new Cursor(StandardCursorType.SizeWestEast)
            : hit is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        if (!Equals(hit, _hover))
        {
            _hover = hit;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_drag.Kind != DragKind.None)
        {
            _drag = default;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _drag = default;
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover is not null)
        {
            _hover = null;
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var editor = _editor;
        if (editor is null || !editor.HasFile)
            return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            // Zoom around the pointer.
            double px = e.GetPosition(this).X;
            double t = T(px + ScrollOffset);
            editor.ZoomLevel = Math.Clamp(editor.ZoomLevel + Math.Sign(e.Delta.Y) * 0.05, 0, 1);
            ScrollOffset = Math.Clamp(X(t) - px, 0, MaxScroll);
        }
        else
        {
            double delta = e.Delta.X != 0 ? e.Delta.X : e.Delta.Y;
            ScrollOffset = Math.Clamp(ScrollOffset - delta * 60, 0, MaxScroll);
        }
        e.Handled = true;
    }

    private enum DragKind
    {
        None,
        Scrub,
        TrimIn,
        TrimOut,
    }

    private readonly record struct Drag(DragKind Kind, ClipViewModel? Clip, double StartX, double Origin);

    private enum HitKind
    {
        KeepGap,
        KeepClip,
        TrimIn,
        TrimOut,
    }

    private sealed record HitRegion(HitKind Kind, ClipViewModel? Clip, double From, double To)
    {
        public Rect Rect { get; init; }
    }
}
