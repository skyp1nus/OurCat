using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.Core.Time;

namespace OurCut.App.ViewModels;

public enum ExportMode
{
    Copy,
    Smart,
    Encode,
}

public sealed partial class ExportModeOption(ExportViewModel owner, ExportMode mode, string title, string tag, string description, bool isAvailable)
    : ViewModelBase
{
    public ExportMode Mode { get; } = mode;
    public string Title { get; } = title;
    public string Tag { get; } = tag;
    public string Description { get; } = description;

    /// <summary>Smart cut is shown but not available in Phase 1.</summary>
    public bool IsAvailable { get; } = isAvailable;

    public bool IsSelected => owner.Mode == Mode;

    internal void Refresh() => OnPropertyChanged(nameof(IsSelected));

    [RelayCommand]
    private void Pick()
    {
        if (IsAvailable)
            owner.Mode = Mode;
    }
}

public sealed partial class ChoiceOption(string label, Action pick) : ViewModelBase
{
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [RelayCommand]
    private void Pick() => pick();
}

public enum ExportRowState
{
    Queued,
    Active,
    Done,
}

/// <summary>One step of a running export: a clip, or the final merge.</summary>
public sealed partial class ExportRowViewModel(string name) : ViewModelBase
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsDone), nameof(IsActive), nameof(IsQueued))]
    public partial ExportRowState State { get; set; }

    /// <summary>Progress of this step, 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial double Progress { get; set; }

    /// <summary>The step shown large in the middle of the list.</summary>
    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Finished and above the current step.</summary>
    [ObservableProperty]
    public partial bool IsPast { get; set; }

    public bool IsDone => State == ExportRowState.Done;
    public bool IsActive => State == ExportRowState.Active;
    public bool IsQueued => State == ExportRowState.Queued;

    public string StatusText => State switch
    {
        ExportRowState.Done => "done",
        ExportRowState.Active => Math.Round(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%",
        _ => "queued",
    };
}

/// <summary>
/// Export dialog: settings, then the list of steps with progress.
/// In this milestone the progress is simulated; the FFmpeg pipeline replaces it later.
/// </summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private static readonly string[] ContainerNames = ["MP4", "MOV", "MKV"];
    private readonly EditorViewModel _editor;
    private DispatcherTimer? _timer;
    private DateTime? _loopHoldStart;

    public ExportViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Modes =
        [
            new(this, ExportMode.Copy, "Lossless copy", "Fastest", "Stream copy, no re-encoding. Cut points snap to the nearest keyframe.", true),
            new(this, ExportMode.Smart, "Smart cut", "Soon", "Re-encodes only the frames around each cut and copies everything else.", false),
            new(this, ExportMode.Encode, "Re-encode", "Slowest", "Full transcode with the codec and quality you choose.", true),
        ];
        Containers = [.. ContainerNames.Select(f => new ChoiceOption(f, () => Container = f))];
        Outputs =
        [
            new("Merge into one file", () => Merge = true),
            new("Separate files", () => Merge = false),
        ];
        RefreshChoices();
    }

    public IReadOnlyList<ExportModeOption> Modes { get; }
    public IReadOnlyList<ChoiceOption> Containers { get; }
    public IReadOnlyList<ChoiceOption> Outputs { get; }
    public ObservableCollection<ExportRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen), nameof(IsConfiguring), nameof(IsExporting))]
    public partial ExportStage Stage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncode), nameof(ModeTitle), nameof(Estimate), nameof(Footer), nameof(Stats))]
    public partial ExportMode Mode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Footer))]
    public partial string Container { get; set; } = "MP4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(FileCountText), nameof(Footer), nameof(CanAddChapters))]
    public partial bool Merge { get; set; } = true;

    [ObservableProperty]
    public partial bool AddChapters { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepAllTracks { get; set; } = true;

    [ObservableProperty]
    public partial string OutputFolder { get; set; } = @"C:\Users\You\Videos\Exports";

    /// <summary>Overall progress 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText), nameof(IsDone), nameof(IsNotDone), nameof(Title), nameof(Stats))]
    public partial double Progress { get; set; }

    /// <summary>Demo only: restart the simulated progress after it completes.</summary>
    public bool Loop { get; set; }

    public bool IsDialogOpen => Stage != ExportStage.Closed;
    public bool IsConfiguring => Stage == ExportStage.Configure;
    public bool IsExporting => Stage == ExportStage.Running;
    public bool IsEncode => Mode == ExportMode.Encode;
    public bool CanAddChapters => Merge;
    public bool IsDone => Progress >= 1;
    public bool IsNotDone => !IsDone;

    public string ModeTitle => Modes.First(m => m.Mode == Mode).Title;
    public string Extension => Container.ToLowerInvariant();
    public string BaseName => _editor.ProjectName;

    public string OutputPath => Merge
        ? Path.Join(OutputFolder, $"{BaseName}-cut.{Extension}").Replace('/', '\\')
        : Path.Join(OutputFolder, $"{BaseName}-{{n}}-{{label}}.{Extension}").Replace('/', '\\');

    public string Summary => $"{Included.Count} clips · {TimeFormat.Duration(Total)} · from {_editor.MediaFileName}";
    public string FileCountText => Merge ? "1 file" : $"{Included.Count} files";
    public string Estimate => $"≈ {SizeGb.ToString("0.00", CultureInfo.InvariantCulture)} GB · about {Secs(Total / Speed)}";
    public string Footer => $"{ModeTitle} · {Container} · {(Merge ? "merged" : "separate files")}";
    public string PercentText => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";
    public string Title => IsDone ? "Export complete" : "Exporting…";

    public string Stats
    {
        get
        {
            double t = Total / Speed;
            string left = IsDone ? "done" : "~" + TimeFormat.Clock((1 - Progress) * t) + " left";
            return $"elapsed {TimeFormat.Clock(Progress * t)} · {left} · {Speed.ToString(CultureInfo.InvariantCulture)}× realtime";
        }
    }

    private List<ClipViewModel> Included => [.. _editor.Clips.Where(c => c.IsIncluded)];
    private double Total => Included.Sum(c => c.Duration);
    private double Speed => Mode switch { ExportMode.Copy => 62, ExportMode.Smart => 18, _ => 1.4 };
    private double SizeGb => Total * (Mode == ExportMode.Encode ? 14 : 22) / 8 / 1000;

    private static string Secs(double x) => x < 60
        ? Math.Max(1, Math.Ceiling(x)).ToString(CultureInfo.InvariantCulture) + " s"
        : Math.Round(x / 60).ToString(CultureInfo.InvariantCulture) + " min";

    partial void OnModeChanged(ExportMode value) => RefreshChoices();
    partial void OnContainerChanged(string value) => RefreshChoices();
    partial void OnMergeChanged(bool value) => RefreshChoices();
    partial void OnProgressChanged(double value) => UpdateRows();

    private void RefreshChoices()
    {
        foreach (var m in Modes)
            m.Refresh();
        foreach (var c in Containers)
            c.IsSelected = c.Label == Container;
        Outputs[0].IsSelected = Merge;
        Outputs[1].IsSelected = !Merge;
        OnPropertyChanged(nameof(Estimate));
    }

    [RelayCommand]
    public void Open()
    {
        if (!_editor.HasFile)
            return;
        Stage = ExportStage.Configure;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(OutputPath));
    }

    [RelayCommand]
    public void Close()
    {
        StopTimer();
        Stage = ExportStage.Closed;
        Progress = 0;
    }

    [RelayCommand]
    public void Start()
    {
        Loop = false;
        Start(0);
    }

    public void Start(double progress)
    {
        BuildRows();
        Stage = ExportStage.Running;
        Progress = progress;
        UpdateRows();
        StopTimer();
        _loopHoldStart = null;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => Tick());
        _timer.Start();
    }

    private void Tick()
    {
        if (Stage != ExportStage.Running)
            return;
        double p = Progress + 0.1 / 9;
        if (p >= 1)
        {
            p = 1;
            if (Loop)
            {
                _loopHoldStart ??= DateTime.UtcNow;
                if (DateTime.UtcNow - _loopHoldStart > TimeSpan.FromSeconds(3))
                {
                    _loopHoldStart = null;
                    p = 0.05;
                }
            }
            else
            {
                StopTimer();
            }
        }
        Progress = p;
    }

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void BuildRows()
    {
        Rows.Clear();
        var on = Included;
        for (int i = 0; i < on.Count; i++)
        {
            string name = Merge
                ? $"{i + 1} · {on[i].Label}"
                : $"{BaseName}-{i + 1}-{Slug(on[i].Label)}.{Extension}";
            Rows.Add(new ExportRowViewModel(name));
        }
        if (Merge)
            Rows.Add(new ExportRowViewModel($"Merge into {BaseName}-cut.{Extension}"));
    }

    /// <summary>Splits overall progress into per-step progress, as the design does.</summary>
    private void UpdateRows()
    {
        if (Rows.Count == 0)
            return;
        var on = Included;
        double total = Total, w = Merge ? 0.92 : 1, acc = 0, p = Progress;
        for (int i = 0; i < on.Count && i < Rows.Count; i++)
        {
            double cw = total > 0 ? on[i].Duration / total * w : 0;
            Rows[i].Progress = cw > 0 ? Math.Clamp((p - acc) / cw, 0, 1) : 1;
            acc += cw;
        }
        if (Merge)
            Rows[^1].Progress = Math.Clamp((p - 0.92) / 0.08, 0, 1);

        int current = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            var r = Rows[i];
            r.State = r.Progress >= 1 ? ExportRowState.Done : r.Progress > 0 ? ExportRowState.Active : ExportRowState.Queued;
            if (current < 0 && r.State != ExportRowState.Done)
                current = i;
        }
        if (current < 0)
            current = Rows.Count - 1;
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsCurrent = i == current;
            Rows[i].IsPast = i < current;
        }
    }

    internal static string Slug(string text) =>
        Regex.Replace(text.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
}

public enum ExportStage
{
    Closed,
    Configure,
    Running,
}
