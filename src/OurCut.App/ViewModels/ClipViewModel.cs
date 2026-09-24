using CommunityToolkit.Mvvm.ComponentModel;
using OurCut.Core.Model;
using OurCut.Core.Time;

namespace OurCut.App.ViewModels;

/// <summary>
/// A clip as shown in the list and on the timeline. Its data mirrors a <see cref="Clip"/> of the
/// Core project; changes go through <see cref="EditorViewModel"/> and come back via
/// <see cref="Update"/>.
/// </summary>
public sealed partial class ClipViewModel : ViewModelBase
{
    private readonly Action<ClipViewModel, bool>? _setIncluded;

    public ClipViewModel(Clip clip, Action<ClipViewModel, bool>? setIncluded = null)
    {
        Id = clip.Id;
        _setIncluded = setIncluded;
        Update(clip);
    }

    public int Id { get; }

    [ObservableProperty]
    public partial string Label { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText), nameof(Duration), nameof(DurationText))]
    public partial double Start { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndText), nameof(Duration), nameof(DurationText))]
    public partial double End { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IncludeToggle))]
    public partial bool IsIncluded { get; private set; }

    /// <summary>Two-way target for the include checkbox; setting it issues an edit.</summary>
    public bool IncludeToggle
    {
        get => IsIncluded;
        set
        {
            if (value != IsIncluded)
                _setIncluded?.Invoke(this, value);
            OnPropertyChanged();
        }
    }

    /// <summary>1-based position in the output order.</summary>
    [ObservableProperty]
    public partial int Number { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>Changed by Claude (shown in violet).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAi), nameof(AiTag))]
    public partial bool IsAiChanged { get; set; }

    /// <summary>Claude is working on this clip right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAi), nameof(AiTag))]
    public partial bool IsAiWorking { get; set; }

    /// <summary>Row being dragged in the clip list.</summary>
    [ObservableProperty]
    public partial bool IsDragSource { get; set; }

    /// <summary>Row under the pointer while another row is dragged.</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    public bool IsAi => IsAiChanged || IsAiWorking;
    public string AiTag => IsAiWorking ? "Claude editing" : "Edited";
    public double Duration => End - Start;
    public string StartText => TimeFormat.MinutesSeconds(Start);
    public string EndText => TimeFormat.MinutesSeconds(End);
    public string DurationText => TimeFormat.Duration(Duration);

    public bool Contains(double t) => t >= Start && t <= End;

    /// <summary>Copies the Core clip's data.</summary>
    public void Update(Clip clip)
    {
        Label = clip.Label;
        Start = clip.Start;
        End = clip.End;
        IsIncluded = clip.IsIncluded;
    }
}
