using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using OurCut.App.Demo;
using OurCut.App.Services;
using OurCut.App.ViewModels;
using OurCut.Transcription.Models;

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
        File.WriteAllText(Path.Combine(models, "whisper-small", "ggml-small.bin"), "");

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
    public void Without_an_installer_models_cannot_be_downloaded()
    {
        var settings = App.CreateEditor(null).Settings;
        var model = settings.Models[0];
        Assert.False(model.CanManage);
        model.DownloadCommand.Execute(null);
        Assert.True(model.IsNotInstalled);
    }

    /// <summary>Serves a file, fails, or holds the request until it is cancelled.</summary>
    private sealed class FakeServer(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);

        public static FakeServer Serving(byte[] bytes) => new(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        }));
    }

    private SettingsViewModel WithInstaller(FakeServer server, out EditorViewModel editor)
    {
        editor = App.CreateEditor(null);
        var settings = editor.Settings;
        settings.ModelsFolder = Directory.CreateDirectory(Path.Combine(_dir, "models")).FullName;
        settings.Installer = new ModelInstaller(new HttpClient(server));
        return settings;
    }

    private static async Task PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 250 && !done(); i++)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
        }
        Assert.True(done());
    }

    [AvaloniaFact]
    public async Task Downloading_a_model_installs_it_and_offers_it()
    {
        var settings = WithInstaller(FakeServer.Serving(new byte[64_000]), out _);
        var small = settings.Models.Single(m => m.Id == "whisper-small");
        Assert.True(small.CanManage);

        small.DownloadCommand.Execute(null);
        Assert.True(small.IsDownloading);
        await PumpUntil(() => small.IsInstalled);

        Assert.Equal(64_000, new FileInfo(Path.Combine(settings.ModelsFolder, "whisper-small", "ggml-small.bin")).Length);
        Assert.Equal("Best available — whisper-small", settings.ModelOptions[0].Label);

        small.DeleteCommand.Execute(null);
        Assert.True(small.IsNotInstalled);
        Assert.False(Directory.Exists(Path.Combine(settings.ModelsFolder, "whisper-small")));
    }

    [AvaloniaFact]
    public async Task A_failed_download_says_why()
    {
        var settings = WithInstaller(new FakeServer(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))), out var editor);
        var model = settings.Models[0];

        model.DownloadCommand.Execute(null);
        await PumpUntil(() => model.Error is not null);

        Assert.True(model.IsNotInstalled);
        Assert.Contains("404", model.Error, StringComparison.Ordinal);
        Assert.StartsWith("Could not download parakeet-tdt-0.6b-v3", editor.StatusMessage, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Cancelling_a_download_removes_what_was_downloaded()
    {
        var settings = WithInstaller(new FakeServer(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }), out _);
        var model = settings.Models.Single(m => m.Id == "whisper-base.en");

        model.DownloadCommand.Execute(null);
        Assert.True(model.IsDownloading);
        model.CancelDownloadCommand.Execute(null);
        await PumpUntil(() => model.IsNotInstalled);

        Assert.Null(model.Error);
        Assert.False(Directory.Exists(Path.Combine(settings.ModelsFolder, "whisper-base.en.partial")));
    }

    [AvaloniaFact]
    public void The_demo_shows_the_designs_models_and_simulates_downloads()
    {
        var settings = App.CreateEditor(DesignScreen.Settings).Settings;
        Assert.True(settings.IsOpen);
        Assert.Equal("Best available — parakeet-tdt-0.6b-v3", settings.Model?.Label);
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
