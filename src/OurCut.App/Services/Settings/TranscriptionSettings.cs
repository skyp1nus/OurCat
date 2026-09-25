namespace OurCut.App.Services;

/// <summary>Settings → Transcription.</summary>
/// <param name="TranscribeOnOpen">Transcribe a video as soon as it is opened.</param>
/// <param name="FillerWords">Filler words by language code ("en", "uk"); null while they are the defaults.</param>
public sealed record TranscriptionSettings(
    string Engine = "Auto",
    string Model = "best",
    string Device = "Auto",
    string Language = "Auto-detect",
    string? ModelsFolder = null,
    bool TranscribeOnOpen = true,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? FillerWords = null);
