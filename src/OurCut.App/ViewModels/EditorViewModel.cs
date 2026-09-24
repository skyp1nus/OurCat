using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;
using OurCut.Core.Editing;
using OurCut.Core.Model;
using OurCut.Core.Serialization;
using OurCut.Core.Time;

namespace OurCut.App.ViewModels;

/// <summary>
/// State of the editor window. The project itself lives in an <see cref="EditorSession"/> from
/// OurCut.Core: every edit is a Core command (undoable, and callable by a future MCP server), and
/// this view model mirrors the resulting project for the views. Playback position, selection and
/// zoom are view state and stay here.
/// </summary>
public sealed partial class EditorViewModel : ViewModelBase
{
    private static readonly double[] Speeds = [0.5, 1, 1.5, 2];
    private readonly Dictionary<HistoryEntry, ClaudeLogItemViewModel> _historyItems = [];
    private DispatcherTimer? _playTimer;
    private DispatcherTimer? _messageTimer;
    private DispatcherTimer? _autosaveTimer;
    private DateTime _lastTick;

    public EditorViewModel()
    {
        Claude = new ClaudePanelViewModel();
        Export = new ExportViewModel(this);
        Session.Changed += OnSessionChanged;
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

    /// <summary>The Core editing session: the project and its undo history.</summary>
    public EditorSession Session { get; } = new();

    public ClaudePanelViewModel Claude { get; }
    public ExportViewModel Export { get; }
    public ObservableCollection<ClipViewModel> Clips { get; } = [];
    public ObservableCollection<AudioLaneViewModel> AudioLanes { get; } = [];
    public ObservableCollection<RecentFileViewModel> RecentFiles { get; } = [];
    public bool HasRecentFiles => RecentFiles.Count > 0;

    /// <summary>File dialogs, provided by the window.</summary>
    public IFileDialogs? Dialogs { get; set; }

    /// <summary>Raised whenever anything drawn on the timeline changes.</summary>
    public event EventHandler? TimelineChanged;

    /// <summary>
    /// Demo mode shows the design's scripted Claude activity in the Claude panel. Otherwise the panel
    /// lists the project's edit history.
    /// </summary>
    public bool IsDemo { get; set; }

    /// <summary>Switches from the scripted demo to a normal project: no MCP connection, history in the Claude panel.</summary>
    public void LeaveDemo()
    {
        if (IsDemo)
            RecentFiles.Clear();
        IsDemo = false;
        Claude.Log.Clear();
        _historyItems.Clear();
        Claude.IsConnected = false;
    }

    // ---- File ----------------------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile), nameof(IsEmpty), nameof(Duration), nameof(DurationText),
        nameof(SourceLengthText), nameof(StatusRight), nameof(FrameText))]
    public partial IMediaPreview? Media { get; set; }

    /// <summary>The timeline ruler is drawn for this duration even before a file is open (demo only).</summary>
    [ObservableProperty]
    public partial double PlaceholderDuration { get; set; }

    [ObservableProperty]
    public partial string MediaFileName { get; set; } = "";

    [ObservableProperty]
    public partial string MediaInfo { get; set; } = "";

    /// <summary>Where the project is saved; null until it is saved once.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight), nameof(WindowTitle))]
    public partial string? ProjectPath { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial bool IsDirty { get; set; }

    /// <summary>Whether the last save happened automatically.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial bool LastSaveWasAuto { get; set; }

    public string ProjectName => Session.Project.Name;
    public bool HasFile => Media is not null && Session.Project.Source is not null;
    public bool IsEmpty => !HasFile;
    public double Duration => HasFile ? Session.Project.SourceDuration : PlaceholderDuration;
    public double FrameRate => Session.Project.Source?.FrameRate is > 0 and var fps ? fps : 30;
    public string DurationText => TimeFormat.Timecode(Duration);
    public string SourceLengthText => HasFile ? TimeFormat.WholeSeconds(Duration) : "no media";
    public string WindowTitle => HasFile ? $"{ProjectName} — OurCut" : "OurCut";

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
    [NotifyPropertyChangedFor(nameof(ExcludeLabel), nameof(HasSelection))]
    public partial ClipViewModel? SelectedClip { get; set; }

    public bool HasSelection => SelectedClip is not null;
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
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial bool SnapToKeyframes { get; set; } = true;

    // ---- Totals and status ---------------------------------------------------------------

    public double OutputDuration => Session.Project.OutputDuration;
    public string TotalText => TimeFormat.Duration(OutputDuration);
    public string ClipCountText => Clips.Count.ToString(CultureInfo.InvariantCulture);
    public bool HasNoClips => Clips.Count == 0;
    public string ClipSummary => $"{Clips.Count(c => c.IsIncluded)} of {Clips.Count} clips";
    public string KeptText => TimeFormat.WholeSeconds(OutputDuration);
    public string ExcludedText => HasFile ? TimeFormat.WholeSeconds(Session.Project.ExcludedDuration) : "0:00";

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

    /// <summary>A short message (e.g. why an edit was refused) shown in the status bar for a few seconds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusRight))]
    public partial string? StatusMessage { get; set; }

    public string SaveStateText => IsDemo ? "autosaved"
        : ProjectPath is null ? "not saved"
        : IsDirty ? "unsaved changes"
        : LastSaveWasAuto ? "autosaved" : "saved";

    public string StatusRight => StatusMessage
        ?? (HasFile
            ? $"{Export.ModeTitle.ToLowerInvariant()} · snap {(SnapToKeyframes ? "on" : "off")} · {SaveStateText}"
            : ToolStatus);

    /// <summary>Source ranges not covered by any clip, in source order.</summary>
    public IReadOnlyList<TimeRange> ExcludedGaps() => HasFile ? Session.Project.UncoveredRanges() : [];

    // ---- Loading -------------------------------------------------------------------------

    /// <summary>Opens a project with its media preview. Clears the undo history.</summary>
    public void LoadProject(Project project, IMediaPreview media, string info, string? projectPath = null)
    {
        StopPlayback();
        Export.Close();
        Select(null);
        MediaFileName = project.Source is { } s ? Path.GetFileName(s.Path) : "";
        MediaInfo = info;
        ProjectPath = projectPath;
        IsDirty = false;
        LastSaveWasAuto = false;
        Session.Keyframes = media.Keyframes;
        Session.Load(project);
        Media = media;
        Claude.HasMedia = true;
        AudioLanes.Clear();
        var tracks = project.Source?.AudioTracks ?? [];
        for (int i = 0; i < tracks.Length; i++)
        {
            var lane = new AudioLaneViewModel(i, "A" + (i + 1).ToString(CultureInfo.InvariantCulture), tracks[i].Label);
            lane.PropertyChanged += (_, _) => RaiseTimelineChanged();
            AudioLanes.Add(lane);
        }
        Time = 0;
        RaiseProjectReplaced();
    }

    /// <summary>Everything derived from the project or its source changes when a project is (un)loaded.</summary>
    private void RaiseProjectReplaced()
    {
        foreach (string name in (string[])[nameof(ProjectName), nameof(WindowTitle), nameof(HasFile), nameof(IsEmpty),
                     nameof(Duration), nameof(DurationText), nameof(SourceLengthText), nameof(FrameRate), nameof(FrameText),
                     nameof(StatusRight)])
            OnPropertyChanged(name);
        RaiseTotals();
        RaiseTimelineChanged();
    }

    public void Unload()
    {
        StopPlayback();
        Export.Close();
        Select(null);
        Media = null;
        MediaFileName = "";
        MediaInfo = "";
        ProjectPath = null;
        IsDirty = false;
        Session.Keyframes = [];
        Session.Load(Project.Empty);
        Claude.HasMedia = false;
        AudioLanes.Clear();
        Time = 0;
        RaiseProjectReplaced();
    }

    // ---- File commands -------------------------------------------------------------------

    /// <summary>Asks the app to open a media file: with a path (drag and drop) or by showing a dialog.</summary>
    public event EventHandler<string?>? OpenRequested;

    /// <summary>Asks the app to open a project file.</summary>
    public event EventHandler<string>? OpenProjectRequested;

    [RelayCommand]
    private void OpenFile() => OpenRequested?.Invoke(this, null);

    public void OpenPath(string path)
    {
        if (path.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            OpenProjectRequested?.Invoke(this, path);
        else
            OpenRequested?.Invoke(this, path);
    }

    [RelayCommand]
    private async Task OpenProject()
    {
        if (Dialogs is null)
            return;
        string? path = await Dialogs.PickProjectToOpenAsync().ConfigureAwait(true);
        if (path is not null)
            OpenProjectRequested?.Invoke(this, path);
    }

    [RelayCommand]
    public async Task SaveProject()
    {
        if (!HasFile)
            return;
        if (ProjectPath is null)
        {
            await SaveProjectAs().ConfigureAwait(true);
            return;
        }
        await SaveToAsync(ProjectPath, auto: false).ConfigureAwait(true);
    }

    [RelayCommand]
    public async Task SaveProjectAs()
    {
        if (!HasFile || Dialogs is null)
            return;
        string? path = await Dialogs.PickProjectSavePathAsync(ProjectFile.FileNameFor(Session.Project)).ConfigureAwait(true);
        if (path is not null)
            await SaveToAsync(path, auto: false).ConfigureAwait(true);
    }

    public async Task SaveToAsync(string path, bool auto)
    {
        var project = Session.Project;
        try
        {
            await ProjectFile.SaveAsync(project, path).ConfigureAwait(true);
            ProjectPath = path;
            IsDirty = !ReferenceEquals(project, Session.Project);
            LastSaveWasAuto = auto;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowMessage("Could not save the project: " + e.Message);
        }
    }

    /// <summary>How long after the last edit a saved project is written again.</summary>
    public TimeSpan AutosaveDelay { get; set; } = TimeSpan.FromSeconds(1.5);

    private void ScheduleAutosave()
    {
        if (ProjectPath is null || IsDemo)
            return;
        _autosaveTimer?.Stop();
        _autosaveTimer = new DispatcherTimer(AutosaveDelay, DispatcherPriority.Background, async (_, _) =>
        {
            _autosaveTimer?.Stop();
            _autosaveTimer = null;
            if (IsDirty && ProjectPath is { } path)
                await SaveToAsync(path, auto: true).ConfigureAwait(true);
        });
        _autosaveTimer.Start();
    }

    // ---- Playback commands ---------------------------------------------------------------

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
    private void ZoomIn() => ZoomLevel = Math.Min(1, ZoomLevel + 0.1);

    [RelayCommand]
    private void ZoomOut() => ZoomLevel = Math.Max(0, ZoomLevel - 0.1);

    public void SetTime(double t) => Time = Math.Clamp(t, 0, Duration);

    // ---- Edit commands (all go through OurCut.Core) --------------------------------------

    [RelayCommand(CanExecute = nameof(CanUndo))]
    public void Undo() => Session.Undo();

    [RelayCommand(CanExecute = nameof(CanRedo))]
    public void Redo() => Session.Redo();

    public bool CanUndo => Session.History.CanUndo;
    public bool CanRedo => Session.History.CanRedo;

    /// <summary>I: move the selected clip's in-point here, or start a new 10 s clip at the playhead.</summary>
    [RelayCommand]
    public void MarkIn()
    {
        if (!HasFile)
            return;
        if (SelectedClip is { } c && Time < c.End - EditRules.MinClipDuration)
        {
            TryEdit(() => Session.SetRange(c.Id, Time, c.End));
            return;
        }
        TryEdit(() =>
        {
            var clip = Session.AddClip(Time, Math.Min(Duration, Time + 10));
            Select(Find(clip.Id));
        });
    }

    /// <summary>O: move the selected clip's out-point here.</summary>
    [RelayCommand]
    public void MarkOut()
    {
        if (SelectedClip is { } c && Time > c.Start + EditRules.MinClipDuration)
            TryEdit(() => Session.SetRange(c.Id, c.Start, Time));
    }

    /// <summary>S: split the selected clip (or the clip under the playhead) at the playhead.</summary>
    [RelayCommand]
    public void Split()
    {
        bool Inside(ClipViewModel x) => Time > x.Start + EditRules.MinClipDuration && Time < x.End - EditRules.MinClipDuration;
        var c = SelectedClip is { } s && Inside(s) ? s : Clips.FirstOrDefault(Inside);
        if (c is null)
            return;
        TryEdit(() => Select(Find(Session.Split(c.Id, Time).Id)));
    }

    /// <summary>E / Del: exclude the selected clip from the export, or keep it again.</summary>
    [RelayCommand]
    public void ToggleExclude()
    {
        if (SelectedClip is { } c)
            TryEdit(() => Session.SetIncluded(c.Id, !c.IsIncluded));
    }

    /// <summary>Shift+Del: remove the selected clip from the project.</summary>
    [RelayCommand]
    public void DeleteClip()
    {
        if (SelectedClip is not { } c)
            return;
        int index = Clips.IndexOf(c);
        if (TryEdit(() => Session.Remove(c.Id)))
            Select(Clips.Count == 0 ? null : Clips[Math.Min(index, Clips.Count - 1)]);
    }

    public void SetIncluded(ClipViewModel clip, bool included) => TryEdit(() => Session.SetIncluded(clip.Id, included));

    /// <summary>"+ Keep" on an excluded gap: add it back as a new clip.</summary>
    public void KeepRange(double from, double to) => TryEdit(() => Select(Find(Session.KeepRange(from, to).Id)));

    /// <summary>"+ Keep" on an excluded clip.</summary>
    public void Keep(ClipViewModel clip)
    {
        TryEdit(() => Session.SetIncluded(clip.Id, true));
        Select(clip);
    }

    public void MoveClip(int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= Clips.Count || to >= Clips.Count)
            return;
        TryEdit(() => Session.Move(Clips[from].Id, to));
    }

    /// <summary>
    /// Drags one end of a clip. Calls with the same <paramref name="mergeKey"/> (one drag) undo as one step.
    /// </summary>
    public void Trim(ClipViewModel clip, bool inPoint, double t, double snapThreshold = 0, string? mergeKey = null) =>
        TryEdit(() => SetTime(Session.Trim(clip.Id, inPoint ? ClipEdge.In : ClipEdge.Out, t,
            SnapToKeyframes ? snapThreshold : 0, mergeKey)));

    public void Select(ClipViewModel? clip)
    {
        SelectedClip = clip;
        foreach (var c in Clips)
            c.IsSelected = ReferenceEquals(c, clip);
        RaiseTimelineChanged();
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

    public ClipViewModel? Find(int id) => Clips.FirstOrDefault(c => c.Id == id);

    private bool TryEdit(Action edit)
    {
        try
        {
            edit();
            return true;
        }
        catch (EditException e)
        {
            ShowMessage(e.Message);
            return false;
        }
    }

    public void ShowMessage(string message)
    {
        StatusMessage = message;
        _messageTimer?.Stop();
        _messageTimer = new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
        {
            StatusMessage = null;
            _messageTimer?.Stop();
        });
        _messageTimer.Start();
    }

    // ---- Syncing from Core ---------------------------------------------------------------

    private void OnSessionChanged(object? sender, ProjectChangedEventArgs e)
    {
        SyncClips(e.Current);
        if (e.Kind != ProjectChangeKind.Loaded)
        {
            IsDirty = true;
            ScheduleAutosave();
        }
        if (!IsDemo)
            SyncHistoryLog();
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        if (e.Previous.Name != e.Current.Name)
        {
            OnPropertyChanged(nameof(ProjectName));
            OnPropertyChanged(nameof(WindowTitle));
        }
        if (Time > Duration)
            SetTime(Duration);
    }

    /// <summary>Updates the clip view models to match the project, keeping them (and selection) by id.</summary>
    private void SyncClips(Project project)
    {
        var ids = project.Clips.Select(c => c.Id).ToHashSet();
        for (int i = Clips.Count - 1; i >= 0; i--)
        {
            if (!ids.Contains(Clips[i].Id))
                Clips.RemoveAt(i);
        }
        for (int i = 0; i < project.Clips.Count; i++)
        {
            var clip = project.Clips[i];
            int at = IndexOfClip(clip.Id);
            if (at < 0)
            {
                Clips.Insert(i, new ClipViewModel(clip, SetIncluded));
                continue;
            }
            Clips[at].Update(clip);
            if (at != i)
                Clips.Move(at, i);
        }
        if (SelectedClip is not null && !Clips.Contains(SelectedClip))
            Select(null);
        RaiseTotals();
    }

    private int IndexOfClip(int id)
    {
        for (int i = 0; i < Clips.Count; i++)
        {
            if (Clips[i].Id == id)
                return i;
        }
        return -1;
    }

    /// <summary>Outside demo mode the Claude panel lists the edit history, with undo and redo per entry.</summary>
    private void SyncHistoryLog()
    {
        var entries = Session.History.Entries;
        var live = entries.ToHashSet();
        foreach (var stale in _historyItems.Keys.Where(k => !live.Contains(k)).ToList())
        {
            Claude.Log.Remove(_historyItems[stale]);
            _historyItems.Remove(stale);
        }
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (!_historyItems.TryGetValue(entry, out var item))
            {
                item = new ClaudeLogItemViewModel(ClaudeLogKind.Action, entry.Description, HistoryMeta(entry), entry.ChangedClipIds,
                    entry.OutputDelta, changed: entry.Origin == EditOrigin.Assistant,
                    setUndone: undone =>
                    {
                        if (undone)
                            Session.UndoThrough(entry);
                        else
                            Session.RedoThrough(entry);
                    });
                _historyItems[entry] = item;
                Claude.Log.Insert(Math.Min(i, Claude.Log.Count), item);
            }
            item.IsUndone = i >= Session.History.Position;
        }
        Claude.Recount();
    }

    private static string HistoryMeta(HistoryEntry entry)
    {
        double d = entry.OutputDelta;
        return Math.Abs(d) < 0.0005
            ? entry.Command.Name
            : $"{entry.Command.Name} · {(d < 0 ? "−" : "+")}{Math.Abs(d).ToString("0.00", CultureInfo.InvariantCulture)} s";
    }

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

    private void OnClipsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ClipViewModel c in e.OldItems)
                c.PropertyChanged -= OnClipPropertyChanged;
        }
        if (e.NewItems is not null)
        {
            foreach (ClipViewModel c in e.NewItems)
                c.PropertyChanged += OnClipPropertyChanged;
        }
        for (int i = 0; i < Clips.Count; i++)
            Clips[i].Number = i + 1;
        RaiseTotals();
    }

    private void OnClipPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ClipViewModel.IsDragSource) or nameof(ClipViewModel.IsDropTarget)
            or nameof(ClipViewModel.IncludeToggle))
            return;
        RaiseTimelineChanged();
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
