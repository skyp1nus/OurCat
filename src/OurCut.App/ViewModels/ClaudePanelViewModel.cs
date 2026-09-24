using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OurCut.App.ViewModels;

public enum ClaudeLogKind
{
    User,
    Claude,
    Action,
    Live,
}

/// <summary>An entry in the Claude activity log: a message, an undoable action, or work in progress.</summary>
public sealed partial class ClaudeLogItemViewModel : ViewModelBase
{
    private readonly Action<bool>? _setUndone;

    public ClaudeLogItemViewModel(ClaudeLogKind kind, string text, string meta = "",
        IReadOnlyList<int>? clipIds = null, double delta = 0, bool changed = false, Action<bool>? setUndone = null)
    {
        Kind = kind;
        Text = text;
        Meta = meta;
        ClipIds = clipIds ?? [];
        Delta = delta;
        IsChange = changed;
        _setUndone = setUndone;
    }

    public ClaudeLogKind Kind { get; }
    public string Text { get; }
    public string Meta { get; }
    public IReadOnlyList<int> ClipIds { get; }

    /// <summary>Change in output duration caused by this action, in seconds.</summary>
    public double Delta { get; }

    /// <summary>Counts as a highlighted (violet) change while not undone.</summary>
    public bool IsChange { get; }

    public bool IsUser => Kind == ClaudeLogKind.User;
    public bool IsClaude => Kind == ClaudeLogKind.Claude;
    public bool IsAction => Kind == ClaudeLogKind.Action;
    public bool IsLive => Kind == ClaudeLogKind.Live;
    public bool CanUndo => IsAction && _setUndone is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UndoLabel), nameof(IsHighlighted))]
    public partial bool IsUndone { get; set; }

    public string UndoLabel => IsUndone ? "Redo" : "Undo";
    public bool IsHighlighted => IsChange && !IsUndone;

    public event EventHandler? UndoToggled;

    [RelayCommand]
    public void ToggleUndo()
    {
        if (_setUndone is null)
            return;
        IsUndone = !IsUndone;
        _setUndone(IsUndone);
        UndoToggled?.Invoke(this, EventArgs.Empty);
    }
}

/// <summary>
/// The collapsible Claude panel. The MCP server is not part of Phase 1, so outside demo mode it
/// shows as disconnected and the composer is disabled.
/// </summary>
public sealed partial class ClaudePanelViewModel : ViewModelBase
{
    public ObservableCollection<ClaudeLogItemViewModel> Log { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsComposerEnabled))]
    public partial bool IsConnected { get; set; }

    /// <summary>A media file is open (Claude needs one to edit).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsComposerEnabled), nameof(ShowIntro))]
    public partial bool HasMedia { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSend))]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial int ChangeCount { get; set; }

    public string StatusText => IsConnected ? "Connected" : "Disconnected";
    public bool IsComposerEnabled => IsConnected && HasMedia;
    public bool CanSend => IsComposerEnabled && !string.IsNullOrWhiteSpace(Draft);
    public bool ShowIntro => !HasMedia && Log.Count == 0;

    public event EventHandler? Changed;

    public ClaudePanelViewModel()
    {
        Log.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ShowIntro));
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
        Changed?.Invoke(this, EventArgs.Empty);
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

    [RelayCommand]
    private void Stop()
    {
        foreach (var live in Log.Where(a => a.IsLive).ToList())
            Log.Remove(live);
        Add(new ClaudeLogItemViewModel(ClaudeLogKind.Claude, "Stopped. Nothing else was changed."));
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
