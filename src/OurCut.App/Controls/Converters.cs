using Avalonia.Data.Converters;

namespace OurCut.App.Controls;

public static class Converters
{
    public static readonly IValueConverter IsPositive = new FuncValueConverter<double, bool>(v => v > 0.5);

    /// <summary>The settings section that is designed (the others are listed only).</summary>
    public static readonly IValueConverter IsTranscription = new FuncValueConverter<string, bool>(v => v == "Transcription");
}
