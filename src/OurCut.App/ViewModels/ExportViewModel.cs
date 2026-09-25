using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;
using OurCut.Core.Time;
using OurCut.Media.Export;
using OurCut.Media.Probing;
using OurCut.Media.Tools;

namespace OurCut.App.ViewModels;

public enum ExportMode
{
    Copy,
    Smart,
    Encode,
}

public sealed partial class ExportModeOption(ExportViewModel owner, ExportMode mode, string title, string tag, string description, bool isAvailable,
    string cardTitle = "", string cardDescription = "")
    : ViewModelBase
{
    /// <summary>Title and one-line description on the mode card of the export dialog.</summary>
    public string CardTitle { get; } = cardTitle;
    public string CardDescription { get; } = cardDescription;

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

/// <summary>An audio setting for re-encoding, with a label that names the source codec for "copy".</summary>
public sealed record AudioChoice(string Label, AudioEncoding Encoding);

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
/// Export dialog: settings, then the list of steps with progress. For a real file the export runs
/// ffmpeg through <see cref="ExportRunner"/> off the UI thread; with the design's sample (demo
/// mode) the progress is simulated.
/// </summary>
public sealed partial class ExportViewModel : ViewModelBase
{
    private static readonly string[] ContainerNames = ["MP4", "MOV", "MKV"];
    private const string DemoFolder = @"C:\Users\You\Videos\Exports";

    private readonly EditorViewModel _editor;
    private DispatcherTimer? _timer;
    private DispatcherTimer? _statsTimer;
    private DateTime? _loopHoldStart;
    private CancellationTokenSource? _exportCts;
    private ExportPlan? _plan;
    private List<(int Step, double From, double To)> _rowWindows = [];
    private IReadOnlyList<string> _written = [];
    private DateTime _started;
    private DateTime? _finished;
    private string? _defaultsFor;

    public ExportViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Modes =
        [
            new(this, ExportMode.Copy, "Lossless copy", "Fastest", "Stream copy, no re-encoding. Cut points snap to the nearest keyframe.", true,
                "Lossless", "Stream copy · keyframe-snapped"),
            new(this, ExportMode.Smart, "Smart cut", "Soon", "Re-encodes only the frames around each cut and copies everything else.", false),
            new(this, ExportMode.Encode, "Re-encode", "Slowest", "Full transcode with the codec and quality you choose.", true,
                "Re-encode", "Frame-accurate · slower"),
        ];
        Containers = [.. ContainerNames.Select(f => new ChoiceOption(f, () => Container = f))];
        Outputs =
        [
            new("Merge into one file", () => Merge = true),
            new("Separate files", () => Merge = false),
        ];
        AudioChoices = [new("Copy (AAC 48 kHz)", AudioEncoding.Copy), new(AudioEncoding.Aac192.Label, AudioEncoding.Aac192)];
        Audio = AudioChoices[0];
        RefreshChoices();
    }

    public IReadOnlyList<ExportModeOption> Modes { get; }

    /// <summary>The modes offered in the dialog (smart cut is not available yet, so it is left out).</summary>
    public IReadOnlyList<ExportModeOption> ModeCards => [.. Modes.Where(m => m.IsAvailable)];

    /// <summary>Container, chapters and track options, folded away by default.</summary>
    [ObservableProperty]
    public partial bool ShowMoreOptions { get; set; }
    public IReadOnlyList<ChoiceOption> Containers { get; }
    public IReadOnlyList<ChoiceOption> Outputs { get; }
    public ObservableCollection<ExportRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AudioChoice> AudioChoices { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDialogOpen), nameof(IsConfiguring), nameof(IsExporting))]
    public partial ExportStage Stage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEncode), nameof(IsCopy), nameof(ModeTitle), nameof(Estimate), nameof(EstimateLine), nameof(Footer), nameof(Stats))]
    public partial ExportMode Mode { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Footer), nameof(FileNamesText))]
    public partial string Container { get; set; } = "MP4";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(FileCountText), nameof(Footer), nameof(CanAddChapters), nameof(FileNamesText))]
    public partial bool Merge { get; set; } = true;

    [ObservableProperty]
    public partial bool AddChapters { get; set; } = true;

    [ObservableProperty]
    public partial bool KeepAllTracks { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimate), nameof(EstimateLine))]
    public partial VideoEncoding Video { get; set; } = VideoEncoding.H264Quality;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Estimate))]
    public partial AudioChoice Audio { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath))]
    public partial string OutputFolder { get; set; } = DemoFolder;

    /// <summary>Overall progress 0..1.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText), nameof(IsDone), nameof(IsNotDone), nameof(IsRunning), nameof(Title), nameof(Stats),
        nameof(ProgressText), nameof(ProgressDetail))]
    public partial double Progress { get; set; }

    /// <summary>Why the export failed; null while it runs or after it succeeds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsRunning), nameof(Title), nameof(Stats), nameof(ProgressText), nameof(ProgressDetail))]
    public partial string? ErrorText { get; set; }

    /// <summary>Waiting for the keyframe scan before the cuts can be planned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Stats), nameof(ProgressText))]
    public partial bool IsPreparing { get; set; }

    /// <summary>Demo only: restart the simulated progress after it completes.</summary>
    public bool Loop { get; set; }

    /// <summary>The design's sample has no file behind it, so its export is simulated.</summary>
    public bool IsSimulated => _editor.IsDemo || Preview is null;

    private MediaPreview? Preview => _editor.Media as MediaPreview;

    public bool IsDialogOpen => Stage != ExportStage.Closed;
    public bool IsConfiguring => Stage == ExportStage.Configure;
    public bool IsExporting => Stage == ExportStage.Running;
    public bool IsEncode => Mode == ExportMode.Encode;
    public bool CanAddChapters => Merge;
    public bool IsDone => Progress >= 1;
    public bool IsNotDone => !IsDone;
    public bool HasError => ErrorText is not null;
    public bool IsRunning => !IsDone && !HasError;

    public string ModeTitle => Modes.First(m => m.Mode == Mode).Title;
    public string Extension => Container.ToLowerInvariant();
    public string BaseName => SafeFileName(_editor.ProjectName);

    public string OutputPath
    {
        get
        {
            if (_plan is not null && !IsSimulated)
                return Merge ? _plan.Outputs[0] : Path.Join(OutputFolder, $"{BaseName}-{{n}}-{{label}}.{Extension}");
            string path = Merge
                ? Path.Join(OutputFolder, $"{BaseName}-cut.{Extension}")
                : Path.Join(OutputFolder, $"{BaseName}-{{n}}-{{label}}.{Extension}");
            // The design shows a Windows path.
            return IsSimulated ? path.Replace('/', '\\') : path;
        }
    }

    public string Summary => $"{Included.Count} clips · {TimeFormat.Duration(Total)} · from {_editor.MediaFileName}";

    /// <summary>Next to the dialog title: "4 clips · 00:04:31.360".</summary>
    public string HeaderSummary => $"{Included.Count} clips · {TimeFormat.Timecode(Total)}";

    public bool IsCopy => Mode == ExportMode.Copy;

    /// <summary>"Separate files (4)".</summary>
    public string SeparateFilesText => $"Separate files ({Included.Count})";

    /// <summary>The file names the export will write.</summary>
    public string FileNamesText
    {
        get
        {
            if (Merge)
                return $"{BaseName}-cut.{Extension}";
            var on = Included;
            return on.Count switch
            {
                0 => "",
                1 => $"{BaseName}-1-{ExportPlanner.Slug(on[0].Label)}.{Extension}",
                _ => $"{BaseName}-1-{ExportPlanner.Slug(on[0].Label)}.{Extension} … {BaseName}-{on.Count}-{ExportPlanner.Slug(on[^1].Label)}.{Extension}",
            };
        }
    }

    /// <summary>
    /// Lossless mode: how far the in-points move back to a keyframe. Out-points are exact.
    /// </summary>
    public string SnapNote
    {
        get
        {
            var keyframes = _editor.Media?.Keyframes ?? [];
            var on = Included;
            if (keyframes.Count == 0)
                return "In points snap back to the previous keyframe. Out points stay exact.";
            var shifts = on.Select(c => c.Start - (keyframes.LastOrDefault(k => k <= c.Start + 1e-6)))
                .Where(d => d > 0.0005).ToList();
            return shifts.Count == 0
                ? "All in points already sit on keyframes."
                : $"{shifts.Count} of {on.Count} in points will snap back to the previous keyframe (up to {shifts.Max().ToString("0.000", CultureInfo.InvariantCulture)} s earlier). Out points stay exact.";
        }
    }

    /// <summary>The line above the progress bar: "Writing clip 2 of 4", "Concatenating 4 clips", "Export complete".</summary>
    public string ProgressText
    {
        get
        {
            if (HasError)
                return "Export failed";
            if (IsDone)
                return "Export complete";
            if (IsPreparing)
                return "Preparing…";
            int clips = Math.Max(1, Merge && Rows.Count > Included.Count ? Rows.Count - 1 : Rows.Count);
            int current = Rows.IndexOf(Rows.FirstOrDefault(r => r.IsCurrent) ?? Rows.LastOrDefault()!) + 1;
            return current > clips ? $"Concatenating {clips} clips" : $"Writing clip {Math.Max(1, current)} of {clips}";
        }
    }

    /// <summary>Under the progress bar: time left and speed, or where the result went.</summary>
    public string ProgressDetail => HasError ? ErrorText! : IsDone ? $"{(Merge ? $"{BaseName}-cut.{Extension}" : FileCountText)} · {OutputFolder}" : Stats;

    /// <summary>The dialog's footer: estimated size and whether anything is re-encoded.</summary>
    public string EstimateLine => Estimate + (Mode == ExportMode.Copy ? " · no re-encode" : " · " + Video.Label.Split(' ')[0] + " re-encode");
    public string FileCountText => Merge ? "1 file" : $"{Included.Count} files";
    public string Footer => $"{ModeTitle} · {Container} · {(Merge ? "merged" : "separate files")}";
    public string PercentText => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";
    public string Title => HasError ? "Export failed" : IsDone ? "Export complete" : IsPreparing ? "Preparing…" : "Exporting…";

    public string Estimate => Preview is { } p && !_editor.IsDemo
        ? $"≈ {Size(EstimatedBytes(p.Info))} · about {Secs(Total / EstimatedSpeed(p.Info))}"
        : $"≈ {SizeGb.ToString("0.00", CultureInfo.InvariantCulture)} GB · about {Secs(Total / Speed)}";

    public string Stats
    {
        get
        {
            if (IsSimulated)
            {
                double t = Total / Speed;
                string left = IsDone ? "done" : "~" + TimeFormat.Clock((1 - Progress) * t) + " left";
                return $"elapsed {TimeFormat.Clock(Progress * t)} · {left} · {Speed.ToString(CultureInfo.InvariantCulture)}× realtime";
            }
            if (HasError)
                return ErrorText!;
            if (IsPreparing)
                return "scanning keyframes";
            double elapsed = ((_finished ?? DateTime.UtcNow) - _started).TotalSeconds;
            string remaining = IsDone ? "done"
                : Progress > 0.02 ? "~" + TimeFormat.Clock(elapsed * (1 - Progress) / Progress) + " left"
                : "estimating";
            double speed = elapsed > 0.5 && _plan is not null ? Progress * _plan.OutputDuration / elapsed : 0;
            string speedText = speed > 0 ? $" · {speed.ToString(speed >= 10 ? "0" : "0.0", CultureInfo.InvariantCulture)}× realtime" : "";
            return $"elapsed {TimeFormat.Clock(elapsed)} · {remaining}{speedText}";
        }
    }

    private List<ClipViewModel> Included => [.. _editor.Clips.Where(c => c.IsIncluded)];
    private double Total => Included.Sum(c => c.Duration);
    private double Speed => Mode switch { ExportMode.Copy => 62, ExportMode.Smart => 18, _ => 1.4 };
    private double SizeGb => Total * (Mode == ExportMode.Encode ? 14 : 22) / 8 / 1000;

    /// <summary>Lossless output is about the source bitrate; re-encoding at these CRFs usually lands below it.</summary>
    private double EstimatedBytes(MediaInfo info)
    {
        double bitrate = info.BitRate > 0 ? info.BitRate : info.Size * 8 / Math.Max(1, info.Duration);
        double factor = Mode != ExportMode.Encode ? 1 : Video == VideoEncoding.H264Fast ? 0.5 : Video == VideoEncoding.H265 ? 0.4 : 0.7;
        return bitrate * factor * Total / 8;
    }

    /// <summary>Rough speed in × realtime: stream copy is disk bound; encoding depends on the pixel rate.</summary>
    private double EstimatedSpeed(MediaInfo info)
    {
        if (Mode != ExportMode.Encode || info.Video is not { } v)
            return 60;
        double pixelRate = Math.Max(1, (double)v.Width * v.Height * Math.Max(1, v.FrameRate));
        double hd = 1920.0 * 1080 * 30;
        double preset = Video == VideoEncoding.H264Fast ? 4 : Video == VideoEncoding.H265 ? 0.5 : 1.5;
        return Math.Max(0.05, preset * hd / pixelRate);
    }

    private static string Size(double bytes) => bytes >= 1e9
        ? (bytes / 1e9).ToString("0.00", CultureInfo.InvariantCulture) + " GB"
        : Math.Max(1, Math.Round(bytes / 1e6)).ToString(CultureInfo.InvariantCulture) + " MB";

    private static string Secs(double x) => x < 60
        ? Math.Max(1, Math.Ceiling(x)).ToString(CultureInfo.InvariantCulture) + " s"
        : Math.Round(x / 60).ToString(CultureInfo.InvariantCulture) + " min";

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        string safe = new string([.. name.Select(ch => invalid.Contains(ch) ? '-' : ch)]).Trim().TrimEnd('.');
        return safe.Length == 0 ? "export" : safe;
    }

    partial void OnModeChanged(ExportMode value) => RefreshChoices();
    partial void OnContainerChanged(string value) => RefreshChoices();
    partial void OnMergeChanged(bool value) => RefreshChoices();

    partial void OnProgressChanged(double value)
    {
        if (IsSimulated)
            UpdateRows();
    }

    private void RefreshChoices()
    {
        foreach (var m in Modes)
            m.Refresh();
        foreach (var c in Containers)
            c.IsSelected = c.Label == Container;
        Outputs[0].IsSelected = Merge;
        Outputs[1].IsSelected = !Merge;
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(EstimateLine));
    }

    [RelayCommand]
    public void Open()
    {
        if (!_editor.HasFile)
            return;
        if (Preview is { } p && !_editor.IsDemo)
            ApplyDefaults(p.Info);
        _plan = null;
        ErrorText = null;
        Stage = ExportStage.Configure;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HeaderSummary));
        OnPropertyChanged(nameof(SeparateFilesText));
        OnPropertyChanged(nameof(FileNamesText));
        OnPropertyChanged(nameof(SnapNote));
        OnPropertyChanged(nameof(Estimate));
        OnPropertyChanged(nameof(EstimateLine));
        OnPropertyChanged(nameof(FileCountText));
        OnPropertyChanged(nameof(OutputPath));
    }

    /// <summary>For each new file: save next to it, in a container that can hold its streams.</summary>
    private void ApplyDefaults(MediaInfo info)
    {
        if (_defaultsFor == info.Path)
            return;
        _defaultsFor = info.Path;
        OutputFolder = Path.GetDirectoryName(info.Path) ?? OutputFolder;
        Container = info.NaturalExtension switch
        {
            "mp4" or "m4v" => "MP4",
            "mov" => "MOV",
            _ => "MKV",
        };
        var first = info.Audio.FirstOrDefault();
        string copy = first is null ? "Copy"
            : $"Copy ({first.Codec.ToUpperInvariant()} {(first.SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture)} kHz)";
        AudioChoices = [new(copy, AudioEncoding.Copy), new(AudioEncoding.Aac192.Label, AudioEncoding.Aac192)];
        Audio = AudioChoices[0];
    }

    [RelayCommand]
    public void Close()
    {
        StopTimer();
        StopStatsTimer();
        StopRunning();
        Stage = ExportStage.Closed;
        Progress = 0;
        ErrorText = null;
        IsPreparing = false;
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (_editor.Dialogs is null)
            return;
        string? folder = await _editor.Dialogs.PickFolderAsync("Save exports to", OutputFolder).ConfigureAwait(true);
        if (folder is not null)
            OutputFolder = folder;
    }

    [RelayCommand]
    private void ToggleMoreOptions() => ShowMoreOptions = !ShowMoreOptions;

    /// <summary>Cancel export: stops it and goes back to the settings.</summary>
    [RelayCommand]
    public void CancelExport()
    {
        StopTimer();
        StopStatsTimer();
        StopRunning();
        Stage = ExportStage.Configure;
        Progress = 0;
        ErrorText = null;
        IsPreparing = false;
        Rows.Clear();
    }

    /// <summary>Cancels a running export; it counts as cancelled at once, before ffmpeg has stopped.</summary>
    private void StopRunning()
    {
        _exportCts?.Cancel();
        if (Outcome == ExportOutcome.Running)
            Outcome = ExportOutcome.Cancelled;
    }

    [RelayCommand]
    private void Reveal()
    {
        if (!IsSimulated)
            FileManager.Reveal(_written.Count > 0 ? _written[0] : OutputFolder);
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        Loop = false;
        if (IsSimulated)
        {
            Start(0);
            return;
        }
        await RunAsync(Preview!).ConfigureAwait(true);
    }

    /// <summary>Starts the simulated export at <paramref name="progress"/> (demo screens).</summary>
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

    /// <summary>The latest real export: running, or how it ended.</summary>
    public ExportOutcome Outcome { get; private set; }

    /// <summary>The files the running export writes, or the latest one wrote.</summary>
    public IReadOnlyList<string> OutputFiles => _written.Count > 0 ? _written : _plan?.Outputs ?? [];

    /// <summary>
    /// Starts an export for Claude (MCP): the dialog's settings, changed where Claude chose otherwise, with the
    /// progress shown in the dialog as if the user had pressed Export. Returns why it cannot start, or null.
    /// </summary>
    public string? StartForClaude(ExportMode? mode = null, string? container = null, bool? merge = null, string? folder = null,
        bool? chapters = null, bool? keepAllTracks = null, VideoEncoding? video = null, bool? copyAudio = null)
    {
        if (!_editor.HasFile)
            return "No video is open.";
        if (IsSimulated)
            return "Exporting is not available for this file.";
        if (Outcome == ExportOutcome.Running)
            return "An export is already running; wait for it (get_export_status) or cancel it.";
        if (Included.Count == 0)
            return "There are no included clips to export.";
        Open();
        if (mode is { } m)
            Mode = m;
        if (container is not null)
            Container = container;
        if (merge is { } mg)
            Merge = mg;
        if (folder is not null)
            OutputFolder = folder;
        if (chapters is { } ch)
            AddChapters = ch;
        if (keepAllTracks is { } all)
            KeepAllTracks = all;
        if (video is not null)
            Video = video;
        if (copyAudio is { } copy)
            Audio = copy ? AudioChoices[0] : AudioChoices[^1];
        _ = StartAsync();
        return null;
    }

    /// <summary>The settings as the Media layer takes them.</summary>
    public ExportSettings BuildSettings(MediaInfo info)
    {
        var tracks = _editor.Session.Project.Source?.AudioTracks ?? [];
        return new ExportSettings
        {
            Mode = Mode switch { ExportMode.Copy => CutMode.Lossless, ExportMode.Smart => CutMode.SmartCut, _ => CutMode.Reencode },
            Container = Container switch { "MOV" => OutputContainer.Mov, "MKV" => OutputContainer.Mkv, _ => OutputContainer.Mp4 },
            Merge = Merge,
            AddChapters = AddChapters && Merge,
            KeepAllTracks = KeepAllTracks,
            // Muted lanes are left out unless every track is kept.
            AudioStreamIndexes = [.. _editor.AudioLanes.Where(l => !l.IsMuted && l.Stream < tracks.Length).Select(l => tracks[l.Stream].Index)],
            OutputFolder = OutputFolder,
            BaseName = BaseName,
            Video = Video,
            Audio = Audio.Encoding,
        };
    }

    private async Task RunAsync(MediaPreview preview)
    {
        var cts = new CancellationTokenSource();
        _exportCts = cts;
        _plan = null;
        _written = [];
        _finished = null;
        ErrorText = null;
        Rows.Clear();
        Progress = 0;
        Stage = ExportStage.Running;
        Outcome = ExportOutcome.Running;
        try
        {
            IsPreparing = Mode == ExportMode.Copy && !preview.KeyframesTask.IsCompleted;
            var keyframes = Mode == ExportMode.Copy
                ? await preview.KeyframesTask.WaitAsync(cts.Token).ConfigureAwait(true)
                : preview.Keyframes;
            IsPreparing = false;

            var plan = ExportPlanner.Plan(_editor.Session.Project, preview.Info, keyframes, BuildSettings(preview.Info));
            _plan = plan;
            BuildRows(plan);
            OnPropertyChanged(nameof(OutputPath));
            _started = DateTime.UtcNow;
            StartStatsTimer();

            var progress = new Progress<ExportProgress>(p =>
            {
                if (ReferenceEquals(_exportCts, cts) && ErrorText is null && _finished is null)
                    ApplyProgress(p);
            });
            _written = await Task.Run(() => ExportRunner.RunAsync(plan, progress, cts.Token), cts.Token).ConfigureAwait(true);
            _finished = DateTime.UtcNow;
            ApplyProgress(new ExportProgress(plan.Steps.Count - 1, 1, 1));
            Outcome = ExportOutcome.Done;
            _editor.ShowMessage(_written.Count == 1 ? $"Exported {Path.GetFileName(_written[0])}" : $"Exported {_written.Count} files");
        }
        catch (OperationCanceledException)
        {
            Outcome = ExportOutcome.Cancelled;
            if (Stage == ExportStage.Running)
                Close();
            _editor.ShowMessage("Export cancelled.");
        }
        catch (Exception e) when (e is MediaToolException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _finished = DateTime.UtcNow;
            IsPreparing = false;
            ErrorText = e.Message;
            Outcome = ExportOutcome.Failed;
        }
        finally
        {
            StopStatsTimer();
            OnPropertyChanged(nameof(Stats));
            if (ReferenceEquals(_exportCts, cts))
                _exportCts = null;
            cts.Dispose();
        }
    }

    private void StartStatsTimer()
    {
        StopStatsTimer();
        _statsTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background,
            (_, _) =>
            {
                OnPropertyChanged(nameof(Stats));
                OnPropertyChanged(nameof(ProgressDetail));
            });
        _statsTimer.Start();
    }

    private void StopStatsTimer()
    {
        _statsTimer?.Stop();
        _statsTimer = null;
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

    /// <summary>Rows for the simulated export, as the design lists them.</summary>
    private void BuildRows()
    {
        Rows.Clear();
        var on = Included;
        for (int i = 0; i < on.Count; i++)
        {
            string name = Merge
                ? $"{i + 1} · {on[i].Label}"
                : $"{BaseName}-{i + 1}-{ExportPlanner.Slug(on[i].Label)}.{Extension}";
            Rows.Add(new ExportRowViewModel(name));
        }
        if (Merge)
            Rows.Add(new ExportRowViewModel($"Merge into {BaseName}-cut.{Extension}"));
    }

    /// <summary>
    /// One row per step of the plan. A re-encoded merge is a single ffmpeg pass, so it is shown as one
    /// row per clip, each covering its share of that pass.
    /// </summary>
    private void BuildRows(ExportPlan plan)
    {
        Rows.Clear();
        var windows = new List<(int, double, double)>();
        if (plan.Steps is [{ Kind: ExportStepKind.EncodeMerged } merged])
        {
            double total = merged.Duration, at = 0;
            foreach (var clip in plan.Clips)
            {
                Rows.Add(new ExportRowViewModel($"{clip.Number} · {clip.Label}"));
                double to = total > 0 ? at + clip.OutputDuration / total : 1;
                windows.Add((0, at, to));
                at = to;
            }
        }
        else
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                Rows.Add(new ExportRowViewModel(plan.Steps[i].Name));
                windows.Add((i, 0, 1));
            }
        }
        _rowWindows = windows;
        SetCurrentRow();
    }

    private void ApplyProgress(ExportProgress p)
    {
        for (int i = 0; i < Rows.Count && i < _rowWindows.Count; i++)
        {
            var (step, from, to) = _rowWindows[i];
            double f = step < p.StepIndex ? 1
                : step > p.StepIndex ? 0
                : to > from ? Math.Clamp((p.StepFraction - from) / (to - from), 0, 1) : 1;
            bool started = step < p.StepIndex || (step == p.StepIndex && p.StepFraction >= from);
            Rows[i].Progress = f;
            Rows[i].State = f >= 1 ? ExportRowState.Done : started ? ExportRowState.Active : ExportRowState.Queued;
        }
        Progress = Math.Clamp(p.Overall, 0, 1);
        SetCurrentRow();
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

        foreach (var r in Rows)
            r.State = r.Progress >= 1 ? ExportRowState.Done : r.Progress > 0 ? ExportRowState.Active : ExportRowState.Queued;
        SetCurrentRow();
    }

    /// <summary>The first unfinished row is shown large; the ones above it are dimmed.</summary>
    private void SetCurrentRow()
    {
        int current = -1;
        for (int i = 0; i < Rows.Count; i++)
        {
            if (current < 0 && Rows[i].State != ExportRowState.Done)
                current = i;
        }
        if (current < 0)
            current = Rows.Count - 1;
        for (int i = 0; i < Rows.Count; i++)
        {
            Rows[i].IsCurrent = i == current;
            Rows[i].IsPast = i < current;
        }
        OnPropertyChanged(nameof(ProgressText));
    }
}

public enum ExportStage
{
    Closed,
    Configure,
    Running,
}

/// <summary>How the latest real export went (what Claude is told).</summary>
public enum ExportOutcome
{
    None,
    Running,
    Done,
    Failed,
    Cancelled,
}
