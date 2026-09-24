using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;
using OurCut.Core.Time;

namespace OurCut.App.ViewModels;

/// <summary>
/// State of the editor window: the open file, its clips, playback and the timeline view.
/// Edits are applied here directly for now; they move to OurCut.Core commands (with undo/redo)
/// in the next milestone.
/// </summary>
public sealed partial class EditorViewModel : ViewModelBase
{
    private static readonly double[] Speeds = [0.5, 1, 1.5, 2];
    private DispatcherTimer? _playTimer;
    private DateTime _lastTick;

    public EditorViewModel()
    {
        Claude = new ClaudePanelViewModel();
        Export = new ExportViewModel(this);
        Clips.CollectionChanged += OnClipsChanged;
        RecentFiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecentFiles));
        Claude.Changed += (_, _) => ApplyClaudeHighlights();
        Claude.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ClaudePanelViewModel.IsBusy))
                OnPropertyChanged(nameof(IsClaudeBusy));
        };
        Export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ExportViewModel.Stage) or nameof(ExportViewModel.Progress) or nameof(ExportViewModel.Mode))
            {
                OnPropertyChanged(nameof(ExportButtonText));
                OnPropertyChanged(nameof(StatusRight));
            }
        };
    }

    public ClaudePanelViewModel Claude { get; }
    public ExportViewModel Export { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ObservableCollection<AudioLaneViewModel> AudioLanes { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>Raised whenever anything drawn on the timeline changes.</summary>
    public event EventHandler? TimelineChanged;

    // ---- File ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(IsEmpty), nameof(Duration), nameof(DurationText),
        nameof(SourceLengthText), nameof(StatusRight), nameof(FrameText))]
    public partial IMediaPreview? Media { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(IsEmpty), nameof(StatusRight))]
    public partial bool IsMediaLoaded { get; set; }

    /// <summary>The timeline ruler is drawn for this duration even before a file is open (demo only).</summary>
    [ObservableProperty]
    public partial double PlaceholderDuration { get; set; }

    [ObservableProperty]
    public partial string ProjectName { get; set; } = "Untitled project";

    [ObservableProperty]
    public partial string MediaFileName { get; set; } = "";

    [ObservableProperty]
    public partial string MediaInfo { get; set; } = "";

    public bool HasFile => IsMediaLoaded && Media is not null;
    public bool IsEmpty => !HasFile;
    public double Duration => HasFile ? Media!.Duration : PlaceholderDuration;
    public double FrameRate => Media?.FrameRate is > 0 and var fps ? fps : 30;
    public string DurationText => TimeFormat.Timecode(Duration);
    public string SourceLengthText => HasFile ? TimeFormat.WholeSeconds(Duration) : "no media";

    // ---- Playback ------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText), nameof(FrameText), nameof(PlayheadText))]
    public partial double Time { get; set; }

    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    public partial double Speed { get; set; } = 1;

    [ObservableProperty]
    public partial double Volume { get; set; } = 0.7;

    public string TimeText => TimeFormat.Timecode(Time);
    public string PlayheadText => TimeFormat.MinutesSeconds(Time);
    public string FrameText => $"{TimeFormat.Timecode(Time)} · f {Math.Round(Time * FrameRate).ToString(CultureInfo.InvariantCulture)}";
    public string SpeedText => Speed.ToString(CultureInfo.InvariantCulture) + "×";

    // ---- Selection -----------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExcludeLabel))]
    public partial ClipViewModel? SelectedClip { get; set; }

    public ClipViewModel? CurrentClip => Clips.FirstOrDefault(c => c.Contains(Time));
    public bool IsInClip => CurrentClip is { IsIncluded: true };
    public bool IsInExcluded => HasFile && !IsInClip;
    public int CurrentClipNumber => CurrentClip?.Number ?? 0;
    public string CurrentClipLabel => CurrentClip?.Label ?? "";
    public bool IsCurrentClipAi => CurrentClip?.IsAi ?? false;
    public string ExcludeLabel => SelectedClip is { IsIncluded: false } ? "Keep" : "Exclude";

    // ---- Timeline view -------------------------------------------------------------------

    /// <summary>Zoom slider position 0..1; the timeline maps it to 1× … frame level.</summary>
    [ObservableProperty]
    public partial double ZoomLevel { get; set; }

    /// <summary>Keyframe snapping while trimming (on by default; the design has no toggle).</summary>
    [ObservableProperty]
    public partial bool SnapToKeyframes { get; set; } = true;

    // ---- Totals --------------------------------------------------------------------------

    public double OutputDuration => Clips.Where(c => c.IsIncluded).Sum(c => c.Duration);
    public string TotalText => TimeFormat.Duration(OutputDuration);
    public string ClipCountText => Clips.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasNoClips => Clips.Count == 0;
    public string ClipSummary => $"{Clips.Count(c => c.IsIncluded)} of {Clips.Count} clips";
    public string KeptText => TimeFormat.WholeSeconds(OutputDuration);

    public string ExcludedText
    {
        get
        {
            if (!HasFile)
                return "0:00";
            double gaps = ExcludedGaps().Sum(g => g.To - g.From);
            double off = Clips.Where(c => !c.IsIncluded).Sum(c => c.Duration);
            return TimeFormat.WholeSeconds(gaps + off);
        }
    }

    public double ClaudeDelta => Claude.Log.Where(a => a.IsHighlighted).Sum(a => a.Delta);
    public bool HasClaudeDelta => ClaudeDelta != 0;

    public string ClaudeDeltaText =>
        (ClaudeDelta < 0 ? "−" : "+") + Math.Abs(ClaudeDelta).ToString("0.00", CultureInfo.InvariantCulture) + " s by Claude";

    public bool IsClaudeBusy => Claude.IsBusy;

    public string ExportButtonText => Export.IsExporting ? "Exporting" : "Export";

    /// <summary>ffmpeg availability shown in the status bar before a file is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial string ToolStatus { get; set; } = "checking ffmpeg…";

    public string StatusRight => HasFile
        ? $"{Export.ModeTitle.ToLowerInvariant()} · snap on · autosaved"
        : ToolStatus;

    /// <summary>Source ranges not covered by any clip, in source order.</summary>
    public IReadOnlyList<(double From, double To)> ExcludedGaps()
    {
        var gaps = new List<(double, double)>();
        double last = 0;
        foreach (var c in Clips.OrderBy(c => c.Start))
        {
            if (c.Start - last > 0.3)
                gaps.Add((last, c.Start));
            last = Math.Max(last, c.End);
        }
        if (Duration - last > 0.3)
            gaps.Add((last, Duration));
        return gaps;
    }

    // ---- Loading -------------------------------------------------------------------------

    public void LoadMedia(IMediaPreview media, string projectName, string fileName, string info,
        IEnumerable<(string Key, string Label)> audioStreams)
    {
        StopPlayback();
        Media = media;
        IsMediaLoaded = true;
        ProjectName = projectName;
        MediaFileName = fileName;
        MediaInfo = info;
        Claude.HasMedia = true;
        AudioLanes.Clear();
        int i = 0;
        foreach (var (key, label) in audioStreams)
        {
            var lane = new AudioLaneViewModel(i++, key, label);
            lane.PropertyChanged += (_, _) => RaiseTimelineChanged();
            AudioLanes.Add(lane);
        }
        RaiseTotals();
    }

    public void Unload()
    {
        StopPlayback();
        Media = null;
        IsMediaLoaded = false;
        ProjectName = "Untitled project";
        MediaFileName = "";
        MediaInfo = "";
        Claude.HasMedia = false;
        Clips.Clear();
        SelectedClip = null;
        Time = 0;
        RaiseTotals();
    }

    // ---- Commands ------------------------------------------------------------------------

    /// <summary>Asks the app to pick and open a file (dialog), or to open the given path.</summary>
    public event EventHandler<string?>? OpenRequested;

    [RelayCommand]
    private void OpenFile() => OpenRequested?.Invoke(this, null);

    public void OpenPath(string path) => OpenRequested?.Invoke(this, path);

    [RelayCommand]
    public void TogglePlay()
    {
        if (!HasFile)
            return;
        if (IsPlaying)
        {
            StopPlayback();
            return;
        }
        IsPlaying = true;
        _lastTick = DateTime.UtcNow;
        _playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
        {
            var now = DateTime.UtcNow;
            double t = Time + (now - _lastTick).TotalSeconds * Speed;
            _lastTick = now;
            if (t >= Duration)
            {
                Time = Duration;
                StopPlayback();
                return;
            }
            SetTime(t);
        });
        _playTimer.Start();
    }

    private void StopPlayback()
    {
        _playTimer?.Stop();
        _playTimer = null;
        IsPlaying = false;
    }

    [RelayCommand]
    private void StepBack() => SetTime(Time - 1 / FrameRate);

    [RelayCommand]
    private void StepForward() => SetTime(Time + 1 / FrameRate);

    public void StepSeconds(double seconds) => SetTime(Time + seconds);

    [RelayCommand]
    private void JumpIn()
    {
        if (SelectedClip is { } c)
            SetTime(c.Start);
    }

    [RelayCommand]
    private void JumpOut()
    {
        if (SelectedClip is { } c)
            SetTime(c.End);
    }

    [RelayCommand]
    private void CycleSpeed()
    {
        int i = Array.IndexOf(Speeds, Speed);
        Speed = Speeds[(i + 1) % Speeds.Length];
    }

    [RelayCommand]
    public void MarkIn()
    {
        if (!HasFile)
            return;
        if (SelectedClip is { } c && Time < c.End - 0.2)
        {
            c.Start = Time;
            return;
        }
        var clip = new ClipViewModel(NextId(), "", Time, Math.Min(Duration, Time + 10));
        clip.Label = "Clip " + clip.Id;
        Clips.Add(clip);
        Select(clip);
    }

    [RelayCommand]
    public void MarkOut()
    {
        if (SelectedClip is { } c && Time > c.Start + 0.2)
            c.End = Time;
    }

    [RelayCommand]
    public void Split()
    {
        var c = SelectedClip is { } s && Time > s.Start + 0.2 && Time < s.End - 0.2
            ? s
            : Clips.FirstOrDefault(x => Time > x.Start + 0.2 && Time < x.End - 0.2);
        if (c is null)
            return;
        int i = Clips.IndexOf(c);
        var second = new ClipViewModel(NextId(), c.Label + " (b)", Time, c.End, c.IsIncluded);
        c.End = Time;
        Clips.Insert(i + 1, second);
        Select(second);
    }

    [RelayCommand]
    public void ToggleExclude()
    {
        if (SelectedClip is { } c)
        {
            c.IsIncluded = !c.IsIncluded;
            OnPropertyChanged(nameof(ExcludeLabel));
        }
    }

    [RelayCommand]
    private void ZoomIn() => ZoomLevel = Math.Min(1, ZoomLevel + 0.1);

    [RelayCommand]
    private void ZoomOut() => ZoomLevel = Math.Max(0, ZoomLevel - 0.1);

    public void SetTime(double t) => Time = Math.Clamp(t, 0, Duration);

    public void Select(ClipViewModel? clip)
    {
        SelectedClip = clip;
        foreach (var c in Clips)
            c.IsSelected = ReferenceEquals(c, clip);
    }

    /// <summary>Clicking a clip row selects it and moves the playhead into it.</summary>
    public void SelectFromList(ClipViewModel clip)
    {
        Select(clip);
        if (!clip.Contains(Time))
            SetTime(clip.Start);
    }

    /// <summary>Scrubbing the timeline moves the playhead and selects the clip under it.</summary>
    public void ScrubTo(double t, bool select)
    {
        SetTime(t);
        if (select && Clips.FirstOrDefault(c => c.Contains(Time)) is { } hit)
            Select(hit);
    }

    /// <summary>Moves a clip's in- or out-point, keeping at least half a second.</summary>
    public void Trim(ClipViewModel clip, bool inPoint, double t)
    {
        if (inPoint)
            clip.Start = Math.Max(0, Math.Min(t, clip.End - 0.5));
        else
            clip.End = Math.Min(Duration, Math.Max(t, clip.Start + 0.5));
        SetTime(inPoint ? clip.Start : clip.End);
    }

    public double SnapToKeyframe(double t, double threshold)
    {
        if (!SnapToKeyframes || Media is null)
            return t;
        double best = t, bestDistance = threshold;
        foreach (double k in Media.Keyframes)
        {
            double d = Math.Abs(k - t);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = k;
            }
        }
        return best;
    }

    /// <summary>"+ Keep" on an excluded gap: add it back as a new clip.</summary>
    public void KeepRange(double from, double to)
    {
        var clip = new ClipViewModel(NextId(), "", from, to);
        clip.Label = "Clip " + clip.Id;
        int i = Clips.ToList().FindIndex(c => c.Start > from);
        Clips.Insert(i < 0 ? Clips.Count : i, clip);
        Select(clip);
    }

    public void Keep(ClipViewModel clip)
    {
        clip.IsIncluded = true;
        Select(clip);
    }

    public void MoveClip(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Clips.Count || to >= Clips.Count)
            return;
        Clips.Move(from, to);
    }

    private int NextId() => Clips.Count == 0 ? 1 : Clips.Max(c => c.Id) + 1;

    // ---- Change tracking -----------------------------------------------------------------

    partial void OnTimeChanged(double value)
    {
        OnPropertyChanged(nameof(CurrentClip));
        OnPropertyChanged(nameof(IsInClip));
        OnPropertyChanged(nameof(IsInExcluded));
        OnPropertyChanged(nameof(CurrentClipNumber));
        OnPropertyChanged(nameof(CurrentClipLabel));
        OnPropertyChanged(nameof(IsCurrentClipAi));
        RaiseTimelineChanged();
    }

    partial void OnZoomLevelChanged(double value) => RaiseTimelineChanged();
    partial void OnSelectedClipChanged(ClipViewModel? value) => RaiseTimelineChanged();

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (ClipViewModel c in e.OldItems)
                c.PropertyChanged -= OnClipPropertyChanged;
        if (e.NewItems is not null)
            foreach (ClipViewModel c in e.NewItems)
                c.PropertyChanged += OnClipPropertyChanged;
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Number = i + 1;
        if (SelectedClip is not null && !Clips.Contains(SelectedClip))
            Select(null);
        RaiseTotals();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipViewModel.IsDragSource) or nameof(ClipViewModel.IsDropTarget))
            return;
        RaiseTotals();
    }

    private void RaiseTotals()
    {
        OnPropertyChanged(nameof(OutputDuration));
        OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(ClipCountText));
        OnPropertyChanged(nameof(HasNoClips));
        OnPropertyChanged(nameof(ClipSummary));
        OnPropertyChanged(nameof(KeptText));
        OnPropertyChanged(nameof(ExcludedText));
        OnPropertyChanged(nameof(ExcludeLabel));
        OnTimeChanged(Time);
    }

    /// <summary>Marks clips changed or being edited by Claude, from the activity log.</summary>
    public void ApplyClaudeHighlights()
    {
        var changed = Claude.Log.Where(a => a.IsHighlighted).SelectMany(a => a.ClipIds).ToHashSet();
        var working = Claude.Log.Where(a => a.IsLive).SelectMany(a => a.ClipIds).ToHashSet();
        foreach (var c in Clips)
        {
            c.IsAiChanged = changed.Contains(c.Id);
            c.IsAiWorking = working.Contains(c.Id);
        }
        OnPropertyChanged(nameof(ClaudeDelta));
        OnPropertyChanged(nameof(HasClaudeDelta));
        OnPropertyChanged(nameof(ClaudeDeltaText));
        OnPropertyChanged(nameof(IsCurrentClipAi));
        RaiseTimelineChanged();
    }

    public void RaiseTimelineChanged() => TimelineChanged?.Invoke(this, EventArgs.Empty);
}
