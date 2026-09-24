using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace OurCut.App;

/// <summary>Geist and Geist Mono (SIL OFL 1.1), embedded from Assets/Fonts.</summary>
public sealed class OurCutFontCollection() : EmbeddedFontCollection(
    new Uri("fonts:OurCut", UriKind.Absolute),
    new Uri("avares://OurCut/Assets/Fonts", UriKind.Absolute));

public static class FontSetup
{
    public const string Sans = "fonts:OurCut#Geist";
    public const string Mono = "fonts:OurCut#Geist Mono";

    public static AppBuilder WithOurCutFonts(this AppBuilder builder) =>
        builder
            .ConfigureFonts(fm => fm.AddFontCollection(new OurCutFontCollection()))
            .With(new FontManagerOptions { DefaultFamilyName = Sans });
}
