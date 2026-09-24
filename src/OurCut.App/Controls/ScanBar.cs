using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace OurCut.App.Controls;

/// <summary>Thin track with a segment (30 % wide) sweeping across it ("cl-scan" in the design).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA1001", Justification = "Cancelled and disposed when detached from the visual tree.")]
public sealed class ScanBar : Control
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ScanBar>();

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<ScanBar, IBrush?>(nameof(Foreground));

    public static readonly StyledProperty<double> PhaseProperty =
        AvaloniaProperty.Register<ScanBar, double>(nameof(Phase));

    private static readonly Animation Sweep = new()
    {
        Duration = TimeSpan.FromSeconds(1.4),
        IterationCount = IterationCount.Infinite,
        Easing = new SineEaseInOut(),
        Children =
        {
            new KeyFrame { Cue = new Cue(0), Setters = { new Setter(PhaseProperty, 0.0) } },
            new KeyFrame { Cue = new Cue(1), Setters = { new Setter(PhaseProperty, 1.0) } },
        },
    };

    private CancellationTokenSource? _cts;

    static ScanBar() => AffectsRender<ScanBar>(BackgroundProperty, ForegroundProperty, PhaseProperty);

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double Phase { get => GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _cts = new CancellationTokenSource();
        _ = Sweep.RunAsync(this, _cts.Token);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDetachedFromVisualTree(e);
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(rect, 1)))
        {
            if (Background is { } bg)
                context.FillRectangle(bg, rect);
            if (Foreground is { } fg)
            {
                // translateX(-100%) → translateX(340%) of a segment 30 % as wide as the track.
                double w = rect.Width * 0.3;
                double x = -w + Phase * 4.4 * w;
                context.DrawRectangle(fg, null, new RoundedRect(new Rect(x, 0, w, rect.Height), 1));
            }
        }
    }
}
