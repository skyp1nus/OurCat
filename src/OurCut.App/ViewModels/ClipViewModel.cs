using CommunityToolkit.Mvvm.ComponentModel;
using OurCut.Core.Time;

namespace OurCut.App.ViewModels;

/// <summary>One kept range of the source file. Its list position is its position in the output.</summary>
public sealed partial class ClipViewModel : ViewModelBase
{
    public ClipViewModel(int id, string label, double start, double end, bool isIncluded = true)
    {
        Id = id;
        Label = label;
        Start = start;
        End = end;
        IsIncluded = isIncluded;
    }

    public int Id { get; }

    [ObservableProperty]
    public partial string Label { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText), nameof(Duration), nameof(DurationText))]
    public partial double Start { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndText), nameof(Duration), nameof(DurationText))]
    public partial double End { get; set; }

    [ObservableProperty]
    public partial bool IsIncluded { get; set; }

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
}
