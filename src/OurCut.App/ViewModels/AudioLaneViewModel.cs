using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OurCut.App.ViewModels;

/// <summary>An audio stream of the source file, shown as a lane under the video.</summary>
public sealed partial class AudioLaneViewModel(int stream, string key, string label) : ViewModelBase
{
    public int Stream { get; } = stream;
    public string Key { get; } = key;
    public string Label { get; } = label;

    /// <summary>Muted in the preview. Muted lanes are drawn at 30 % opacity.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MuteTip))]
    public partial bool IsMuted { get; set; }

    public string MuteTip => IsMuted ? "Unmute" : "Mute";

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;
}
