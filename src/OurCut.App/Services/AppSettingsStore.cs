using System.Text.Json;
using System.Text.Json.Serialization;

namespace OurCut.App.Services;

/// <summary>Transcription settings (Settings → Transcription). Transcription itself is not part of Phase 1.</summary>
public sealed record TranscriptionSettings(
    string Engine = "Auto",
    string Model = "best",
    string Device = "Auto",
    string Language = "Auto-detect",
    string? ModelsFolder = null);

/// <summary>Everything the settings dialog saves.</summary>
public sealed record AppSettings(TranscriptionSettings Transcription)
{
    public static AppSettings Default { get; } = new(new TranscriptionSettings());
}

/// <summary>Keeps <see cref="AppSettings"/> in a small JSON file in the user's app data.</summary>
public sealed class AppSettingsStore(string file)
{
    public static string DefaultFile { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OurCut", "settings.json");

    /// <summary>Where transcription models go unless the user picks another folder.</summary>
    public static string DefaultModelsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OurCut", "models");

    public string File { get; } = file;

    /// <summary>The saved settings; a missing or broken file reads as the defaults.</summary>
    public AppSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(File))
                return AppSettings.Default;
            return JsonSerializer.Deserialize(System.IO.File.ReadAllBytes(File), AppSettingsJson.Default.AppSettings) is { Transcription: not null } s
                ? s
                : AppSettings.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return AppSettings.Default;
        }
    }

    /// <summary>Saves atomically. Failing to save settings is not worth an error message.</summary>
    public void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            string temp = File + ".tmp";
            System.IO.File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(settings, AppSettingsJson.Default.AppSettings));
            System.IO.File.Move(temp, File, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJson : JsonSerializerContext;
