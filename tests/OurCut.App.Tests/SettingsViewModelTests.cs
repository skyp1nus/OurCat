using Avalonia.Headless.XUnit;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;

namespace OurCut.App.Tests;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ourcut-settings").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [AvaloniaFact]
    public void Choices_are_saved_as_they_change_and_read_back()
    {
        var store = new AppSettingsStore(Path.Combine(_dir, "settings.json"));
        var settings = App.CreateEditor(null).Settings;
        settings.Store = store;
        string models = Directory.CreateDirectory(Path.Combine(_dir, "models")).FullName;
        Directory.CreateDirectory(Path.Combine(models, "whisper-small"));

        settings.ModelsFolder = models;
        settings.EngineOptions.Single(o => o.Label == "Whisper").PickCommand.Execute(null);
        settings.Device = "CPU";
        settings.Language = "Ukrainian";

        var saved = store.Load().Transcription;
        Assert.Equal(("Whisper", "whisper-small", "CPU", "Ukrainian", models), (saved.Engine, saved.Model, saved.Device, saved.Language, saved.ModelsFolder));

        var reloaded = App.CreateEditor(null).Settings;
        reloaded.Load(store.Load());
        Assert.Equal("Whisper", reloaded.Engine);
        Assert.True(reloaded.EngineOptions.Single(o => o.Label == "Whisper").IsSelected);
        Assert.Equal("whisper-small", reloaded.Model?.Value);
        Assert.True(reloaded.Models.Single(m => m.Id == "whisper-small").IsInstalled);
    }

    [AvaloniaFact]
    public void Outside_demo_mode_models_cannot_be_downloaded_yet()
    {
        var settings = App.CreateEditor(null).Settings;
        var model = settings.Models[0];
        Assert.False(model.CanManage);
        model.DownloadCommand.Execute(null);
        Assert.True(model.IsNotInstalled);
    }

    [AvaloniaFact]
    public void The_demo_shows_the_designs_models_and_simulates_downloads()
    {
        var settings = App.CreateEditor(DesignScreen.Settings).Settings;
        Assert.True(settings.IsOpen);
        Assert.Equal("Best available — parakeet-tdt-0.6b-v2", settings.Model?.Label);
        Assert.Equal("212 GB free", settings.DiskFreeText);

        var small = settings.Models.Single(m => m.Id == "whisper-small");
        Assert.True(small.IsDownloading);
        for (int i = 0; i < 70; i++)
            settings.TickDownloads();
        Assert.True(small.IsInstalled);

        var medium = settings.Models.Single(m => m.Id == "whisper-medium");
        medium.DownloadCommand.Execute(null);
        Assert.True(medium.IsDownloading);
        medium.CancelDownloadCommand.Execute(null);
        Assert.True(medium.IsNotInstalled);

        settings.Models[0].DeleteCommand.Execute(null);
        Assert.Equal("Best available — whisper-large-v3-turbo", settings.ModelOptions[0].Label);
    }

    [AvaloniaFact]
    public void A_broken_settings_file_reads_as_the_defaults()
    {
        string file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, "{ not json");
        Assert.Equal(AppSettings.Default, new AppSettingsStore(file).Load());
    }
}
