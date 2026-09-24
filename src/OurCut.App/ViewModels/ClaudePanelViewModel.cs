using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.Core.Editing;

namespace OurCut.App.ViewModels;

public enum ClaudeLogKind
{
    User,
    Claude,
    Action,
    Live,
}

/// <summary>
/// An entry in the Claude activity log. Actions and work in progress are shown as small cards
/// ("Removed 3 silences", detail line, "18 min ago", Undo); messages are kept but not shown.
/// </summary>
public sealed partial class ClaudeLogItemViewModel : ViewModelBase
{
    private readonly Action<bool>? _setUndone;

    public ClaudeLogItemViewModel(ClaudeLogKind kind, string text, string meta = "",
        IReadOnlyList<int>? clipIds = null, double delta = 0, bool changed = false, Action<bool>? setUndone = null,
        DateTimeOffset? at = null)
    {
        Kind = kind;
        Text = text;
        Meta = meta;
        ClipIds = clipIds ?? [];
        Delta = delta;
        IsChange = changed;
        _setUndone = setUndone;
        At = at ?? DateTimeOffset.Now;
        Now = At;
    }

    public ClaudeLogKind Kind { get; }
    public string Text { get; }
    public string Meta { get; }
    public IReadOnlyList<int> ClipIds { get; }

    /// <summary>When the action happened.</summary>
    public DateTimeOffset At { get; }

    /// <summary>Change in output duration caused by this action, in seconds.</summary>
    public double Delta { get; }

    /// <summary>Counts as a highlighted change (Claude's edit) while not undone.</summary>
    public bool IsChange { get; }

    public bool IsUser => Kind == ClaudeLogKind.User;
    public bool IsClaude => Kind == ClaudeLogKind.Claude;
    public bool IsAction => Kind == ClaudeLogKind.Action;
    public bool IsLive => Kind == ClaudeLogKind.Live;

    /// <summary>Shown as a card: an action or work in progress.</summary>
    public bool IsCard => IsAction || IsLive;

    public bool CanUndo => IsAction && _setUndone is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoLabel), nameof(IsHighlighted), nameof(ShowUndo), nameof(ShowUndone))]
    public partial bool IsUndone { get; set; }

    public string UndoLabel => IsUndone ? "Redo" : "Undo";
    public bool IsHighlighted => IsChange && !IsUndone;

    /// <summary>The card's Undo button.</summary>
    public bool ShowUndo => CanUndo && !IsUndone;

    /// <summary>The card's "Undone" label.</summary>
    public bool ShowUndone => IsAction && IsUndone;

    /// <summary>The clock used for <see cref="WhenText"/>; the panel moves it forward.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WhenText))]
    public partial DateTimeOffset Now { get; set; }

    /// <summary>"now" for work in progress, otherwise "just now", "42s ago" or "18 min ago".</summary>
    public string WhenText
    {
        get
        {
            if (IsLive)
                return "now";
            double s = Math.Max(0, Math.Round((Now - At).TotalSeconds));
            return s < 5 ? "just now"
                : s < 60 ? $"{s:0}s ago"
                : s < 3600 ? $"{Math.Round(s / 60):0} min ago"
                : At.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture);
        }
    }

    public event EventHandler? UndoToggled;

    [RelayCommand]
    public void ToggleUndo()
    {
        if (_setUndone is null)
            return;
        IsUndone = !IsUndone;
        try
        {
            _setUndone(IsUndone);
        }
        catch (EditException)
        {
            // Refused (a later edit changed the same clips): the card stays as it was.
            IsUndone = !IsUndone;
            return;
        }
        UndoToggled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Undo on the card: reverts just this action.</summary>
    [RelayCommand]
    public void Undo()
    {
        if (ShowUndo)
            ToggleUndo();
    }
}

/// <summary>
/// The collapsible Claude panel: a log of Claude's timeline edits, each with its own undo.
/// The MCP server is not part of Phase 1, so outside demo mode it shows as not running and the
/// log lists the project's edit history instead. Claude is talked to through its own MCP client,
/// so the panel has no message box.
/// </summary>
public sealed partial class ClaudePanelViewModel : ViewModelBase
{
    public ObservableCollection<ClaudeLogItemViewModel> Log { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine))]
    public partial bool IsOpen { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsComposerEnabled), nameof(McpText))]
    public partial bool IsConnected { get; set; }

    /// <summary>A media file is open (Claude needs one to edit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerEnabled), nameof(ShowIntro), nameof(StatusLine))]
    public partial bool HasMedia { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLine), nameof(McpText))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int ChangeCount { get; set; }

    public string StatusText => IsConnected ? "Connected" : "Disconnected";
    public bool IsComposerEnabled => IsConnected && HasMedia;
    public bool CanSend => IsComposerEnabled && !string.IsNullOrWhiteSpace(Draft);
    public bool ShowIntro => !HasMedia && Log.Count == 0;

    /// <summary>Nothing to show in the log yet.</summary>
    public bool HasNoCards => !Log.Any(a => a.IsCard);

    /// <summary>The MCP badge in the title bar.</summary>
    public string McpText => !IsConnected ? "MCP · not running" : IsBusy ? "MCP · Claude editing" : "MCP · Claude connected";

    /// <summary>Right side of the panel header: "Editing timeline", "Idle · 4 actions", "Waiting for a video".</summary>
    public string StatusLine => IsBusy ? "Editing timeline"
        : !HasMedia ? "Waiting for a video"
        : IsOpen ? "Idle"
        : $"Idle · {Log.Count(a => a.IsAction && !a.IsUndone)} actions";

    public event EventHandler? Changed;

    public ClaudePanelViewModel()
    {
        Log.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowIntro));
            OnPropertyChanged(nameof(HasNoCards));
            Recount();
        };
    }

    public void Add(ClaudeLogItemViewModel item)
    {
        item.UndoToggled += (_, _) => Recount();
        Log.Add(item);
    }

    public void Recount()
    {
        ChangeCount = Log.Count(a => a.IsHighlighted);
        IsBusy = Log.Any(a => a.IsLive);
        OnPropertyChanged(nameof(StatusLine));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the clock of every card to <paramref name="now"/> ("18 min ago").</summary>
    public void RefreshTimes(DateTimeOffset now)
    {
        foreach (var item in Log)
            item.Now = now;
    }

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    private void ToggleConnection() => IsConnected = !IsConnected;

    [RelayCommand]
    private void UndoAll()
    {
        foreach (var a in Log.Where(a => a.IsHighlighted).ToList())
            a.ToggleUndo();
    }

    /// <summary>Stops the work in progress; nothing it has not applied yet is changed.</summary>
    [RelayCommand]
    private void Stop()
    {
        foreach (var live in Log.Where(a => a.IsLive).ToList())
            Log.Remove(live);
    }

    [RelayCommand]
    private void Send()
    {
        if (!CanSend)
            return;
        Add(new ClaudeLogItemViewModel(ClaudeLogKind.User, Draft.Trim()));
        Draft = "";
    }
}
