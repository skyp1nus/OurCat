using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;

namespace OurCut.App.ViewModels;

public enum ModelState
{
    NotInstalled,
    Downloading,
    Installed,
}

/// <summary>A transcription model in the Settings → Transcription table.</summary>
public sealed partial class TranscriptionModelViewModel(SettingsViewModel owner, string id, string engine, string size)
    : ViewModelBase
{
    public string Id { get; } = id;
    public string Engine { get; } = engine;
    public string Size { get; } = size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstalled), nameof(IsDownloading), nameof(IsNotInstalled))]
    public partial ModelState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    public partial double Progress { get; set; }

    public bool IsInstalled => State == ModelState.Installed;
    public bool IsDownloading => State == ModelState.Downloading;
    public bool IsNotInstalled => State == ModelState.NotInstalled;
    public string PercentText => Math.Floor(Progress * 100).ToString(CultureInfo.InvariantCulture) + "%";

    /// <summary>Downloads and deletes only work in demo mode until transcription exists.</summary>
    public bool CanManage => owner.CanManageModels;

    [RelayCommand]
    private void Download() => owner.Download(this);

    [RelayCommand]
    private void Delete() => owner.Delete(this);

    [RelayCommand]
    private void CancelDownload() => owner.Delete(this);
}

/// <summary>
/// The settings dialog: Transcription (as designed) and MCP server (how to connect Claude); the other
/// sections are listed but empty. Choices are saved as soon as they change. Transcription itself comes
/// later, so outside demo mode the model table only reports which models are in the models folder.
/// </summary>
public sealed partial class SettingsViewModel : ViewModelBase
{
    private static readonly (string Id, string Engine, string Size)[] Catalog =
    [
        ("parakeet-tdt-0.6b-v2", "Parakeet", "1.2 GB"),
        ("whisper-large-v3-turbo", "Whisper", "1.6 GB"),
        ("whisper-medium", "Whisper", "1.5 GB"),
        ("whisper-small", "Whisper", "488 MB"),
        ("whisper-base.en", "Whisper", "148 MB"),
    ];

    public static IReadOnlyList<string> Sections { get; } = ["General", "Playback", "Export", "Transcription", "Keyboard", "MCP server"];
    public static IReadOnlyList<string> Engines { get; } = ["Auto", "Parakeet", "Whisper"];
    public static IReadOnlyList<string> Devices { get; } = ["Auto", "GPU", "CPU"];
    public static IReadOnlyList<string> Languages { get; } =
        ["Auto-detect", "English", "German", "Spanish", "French", "Japanese", "Portuguese", "Ukrainian"];

    private readonly EditorViewModel _editor;
    private DispatcherTimer? _downloadTimer;
    private bool _loading;

    public SettingsViewModel(EditorViewModel editor)
    {
        _editor = editor;
        Models = [.. Catalog.Select(m => new TranscriptionModelViewModel(this, m.Id, m.Engine, m.Size))];
        SectionOptions = [.. Sections.Select(name => new ChoiceOption(name, () => Section = name))];
        EngineOptions = [.. Engines.Select(e => new ChoiceOption(e, () => Engine = e))];
        DeviceOptions = [.. Devices.Select(d => new ChoiceOption(d, () => Device = d))];
        Load(AppSettings.Default);
        OnSectionChanged(Section);
        var (command, args) = EditorLauncher.McpCommand();
        McpCommand = command;
        McpArgs = args;
    }

    /// <summary>Where settings are saved; null keeps them in memory (demo mode and tests).</summary>
    public AppSettingsStore? Store { get; set; }

    /// <summary>Claude's connection, for the MCP server section.</summary>
    public ClaudePanelViewModel Claude => _editor.Claude;

    /// <summary>Puts text on the clipboard; provided by the window.</summary>
    public Func<string, Task>? CopyText { get; set; }

    public IReadOnlyList<ChoiceOption> SectionOptions { get; }

    /// <summary>The section shown on the right.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTranscription), nameof(IsMcp), nameof(IsEmptySection), nameof(SectionNote))]
    public partial string Section { get; set; } = "Transcription";

    public bool IsTranscription => Section == "Transcription";
    public bool IsMcp => Section == "MCP server";
    public bool IsEmptySection => !IsTranscription && !IsMcp;

    public string SectionNote => Section switch
    {
        "Transcription" => "Runs locally. Claude uses the transcript to find silences, quotes and filler words.",
        "MCP server" => "Lets Claude read and edit the timeline. Every edit shows up in the Claude panel and can be undone.",
        _ => "Nothing to set here yet.",
    };

    public ObservableCollection<TranscriptionModelViewModel> Models { get; }
    public IReadOnlyList<ChoiceOption> EngineOptions { get; }
    public IReadOnlyList<ChoiceOption> DeviceOptions { get; }

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string Engine { get; set; } = "Auto";

    [ObservableProperty]
    public partial string Device { get; set; } = "Auto";

    [ObservableProperty]
    public partial string Language { get; set; } = "Auto-detect";

    [ObservableProperty]
    public partial string ModelsFolder { get; set; } = "";

    /// <summary>Selected model id, or "best" for the best installed one (Auto engine only).</summary>
    [ObservableProperty]
    public partial ModelOption? Model { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ModelOption> ModelOptions { get; private set; } = [];

    public bool CanManageModels => _editor.IsDemo;

    public string DeviceNote => _editor.IsDemo
        ? (Device == "CPU" ? "16 threads · ~4× slower" : "NVIDIA RTX 4070 · CUDA 12.4")
        : Device switch
        {
            "GPU" => "Needs a CUDA or DirectML capable GPU",
            "CPU" => "Works everywhere, several times slower",
            _ => "Uses the GPU when one is available",
        };

    public string DiskFreeText
    {
        get
        {
            if (_editor.IsDemo)
                return "212 GB free";
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(ModelsFolder));
                if (root is null)
                    return "";
                double gb = new DriveInfo(root).AvailableFreeSpace / 1e9;
                return gb.ToString("0", CultureInfo.InvariantCulture) + " GB free";
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
                return "";
            }
        }
    }

    public void Load(AppSettings settings)
    {
        _loading = true;
        var t = settings.Transcription;
        Engine = Engines.Contains(t.Engine) ? t.Engine : "Auto";
        Device = Devices.Contains(t.Device) ? t.Device : "Auto";
        Language = Languages.Contains(t.Language) ? t.Language : "Auto-detect";
        ModelsFolder = string.IsNullOrWhiteSpace(t.ModelsFolder) ? AppSettingsStore.DefaultModelsFolder : t.ModelsFolder;
        ScanModels();
        RefreshModelOptions(t.Model);
        SyncChoices();
        _loading = false;
    }

    private void SyncChoices()
    {
        foreach (var o in EngineOptions)
            o.IsSelected = o.Label == Engine;
        foreach (var o in DeviceOptions)
            o.IsSelected = o.Label == Device;
    }

    /// <summary>Shows the design's sample settings (demo mode).</summary>
    public void LoadDemo()
    {
        _loading = true;
        Engine = "Auto";
        Device = "Auto";
        Language = "Auto-detect";
        ModelsFolder = @"D:\OurCut\models";
        foreach (var m in Models)
        {
            (m.State, m.Progress) = m.Id switch
            {
                "parakeet-tdt-0.6b-v2" or "whisper-large-v3-turbo" => (ModelState.Installed, 1.0),
                "whisper-small" => (ModelState.Downloading, 0.64),
                _ => (ModelState.NotInstalled, 0.0),
            };
        }
        RefreshModelOptions("best");
        SyncChoices();
        _loading = false;
        OnPropertyChanged(nameof(DeviceNote));
        OnPropertyChanged(nameof(DiskFreeText));
        StartDownloadTimer();
    }

    public AppSettings ToSettings() =>
        new(new TranscriptionSettings(Engine, Model?.Value ?? "best", Device, Language,
            ModelsFolder == AppSettingsStore.DefaultModelsFolder ? null : ModelsFolder));

    // ---- MCP server ----------------------------------------------------------------------

    /// <summary>The program Claude starts (this OurCut) and its arguments.</summary>
    public string McpCommand { get; }
    public IReadOnlyList<string> McpArgs { get; }

    /// <summary>Adds OurCut to Claude Code, for every project.</summary>
    public string ClaudeCodeCommand => "claude mcp add --scope user ourcut -- " + string.Join(' ', McpArgs.Prepend(McpCommand).Select(Quote));

    /// <summary>The entry for Claude Desktop's claude_desktop_config.json.</summary>
    public string ClaudeDesktopConfig =>
        JsonSerializer.Serialize(new { mcpServers = new { ourcut = new { command = McpCommand, args = McpArgs } } }, IndentedJson);

    /// <summary>Where Claude Desktop keeps its settings on this system.</summary>
    public static string ClaudeDesktopConfigPath =>
        OperatingSystem.IsWindows() ? @"%APPDATA%\Claude\claude_desktop_config.json"
        : OperatingSystem.IsMacOS() ? "~/Library/Application Support/Claude/claude_desktop_config.json"
        : "~/.config/Claude/claude_desktop_config.json";

    // Relaxed escaping keeps non-ASCII paths readable (no \uXXXX); the text is only pasted into a JSON file.
    private static readonly JsonSerializerOptions IndentedJson = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Quote(string arg) =>
        arg.Length > 0 && !arg.Any(ch => char.IsWhiteSpace(ch) || ch is '"' or '\'' or '&' or '(' or ')' or ';') ? arg
        : OperatingSystem.IsWindows() ? '"' + arg + '"'
        : "'" + arg.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    [RelayCommand]
    private Task CopyClaudeCode() => CopyText?.Invoke(ClaudeCodeCommand) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyClaudeDesktop() => CopyText?.Invoke(ClaudeDesktopConfig) ?? Task.CompletedTask;

    partial void OnSectionChanged(string value)
    {
        foreach (var o in SectionOptions)
            o.IsSelected = o.Label == value;
    }

    [RelayCommand]
    public void Open()
    {
        if (!_editor.IsDemo)
            ScanModels();
        OnPropertyChanged(nameof(DiskFreeText));
        IsOpen = true;
    }

    [RelayCommand]
    public void Close() => IsOpen = false;

    [RelayCommand]
    private async Task ChangeFolder()
    {
        if (_editor.Dialogs is null)
            return;
        string? folder = await _editor.Dialogs.PickFolderAsync("Transcription models folder", ModelsFolder).ConfigureAwait(true);
        if (folder is not null)
            ModelsFolder = folder;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!_editor.IsDemo && Directory.Exists(ModelsFolder))
            FileManager.Reveal(ModelsFolder);
    }

    internal void Download(TranscriptionModelViewModel model)
    {
        if (!CanManageModels || !model.IsNotInstalled)
            return;
        model.Progress = 0;
        model.State = ModelState.Downloading;
        StartDownloadTimer();
    }

    internal void Delete(TranscriptionModelViewModel model)
    {
        if (!CanManageModels)
            return;
        model.State = ModelState.NotInstalled;
        model.Progress = 0;
        RefreshModelOptions(Model?.Value);
    }

    /// <summary>Advances simulated downloads (demo mode), 0.6 % every 150 ms like the prototype.</summary>
    public void TickDownloads()
    {
        bool finished = false;
        foreach (var m in Models.Where(m => m.IsDownloading))
        {
            m.Progress = Math.Min(1, m.Progress + 0.006);
            if (m.Progress >= 1)
            {
                m.State = ModelState.Installed;
                finished = true;
            }
        }
        if (finished)
            RefreshModelOptions(Model?.Value);
        if (!Models.Any(m => m.IsDownloading))
            StopDownloadTimer();
    }

    private void StartDownloadTimer()
    {
        if (_downloadTimer is not null || !Models.Any(m => m.IsDownloading))
            return;
        _downloadTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => TickDownloads());
        _downloadTimer.Start();
    }

    private void StopDownloadTimer()
    {
        _downloadTimer?.Stop();
        _downloadTimer = null;
    }

    /// <summary>Marks the models found in the models folder (a file or folder named after the model) as installed.</summary>
    private void ScanModels()
    {
        if (_editor.IsDemo)
            return;
        foreach (var m in Models)
        {
            bool found = false;
            try
            {
                found = Directory.Exists(Path.Combine(ModelsFolder, m.Id))
                        || (Directory.Exists(ModelsFolder) && Directory.EnumerateFiles(ModelsFolder, m.Id + ".*").Any());
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
            {
            }
            m.State = found ? ModelState.Installed : ModelState.NotInstalled;
            m.Progress = found ? 1 : 0;
        }
    }

    /// <summary>Installed models of the chosen engine; "Best available" first when the engine is Auto.</summary>
    private void RefreshModelOptions(string? keep)
    {
        var installed = Models.Where(m => m.IsInstalled && (Engine == "Auto" || m.Engine == Engine)).ToList();
        var options = new List<ModelOption>();
        if (Engine == "Auto")
            options.Add(new ModelOption("best", "Best available — " + (installed.FirstOrDefault()?.Id ?? "none installed")));
        options.AddRange(installed.Select(m => new ModelOption(m.Id, m.Id)));
        bool wasLoading = _loading;
        _loading = true;
        ModelOptions = options;
        Model = options.FirstOrDefault(o => o.Value == keep) ?? options.FirstOrDefault();
        _loading = wasLoading;
    }

    partial void OnEngineChanged(string value)
    {
        foreach (var o in EngineOptions)
            o.IsSelected = o.Label == value;
        RefreshModelOptions(value == "Auto" ? "best" : null);
        Save();
    }

    partial void OnDeviceChanged(string value)
    {
        foreach (var o in DeviceOptions)
            o.IsSelected = o.Label == value;
        OnPropertyChanged(nameof(DeviceNote));
        Save();
    }

    partial void OnLanguageChanged(string value) => Save();

    partial void OnModelChanged(ModelOption? value) => Save();

    partial void OnModelsFolderChanged(string value)
    {
        if (!_loading)
            ScanModels();
        RefreshModelOptions(Model?.Value);
        OnPropertyChanged(nameof(DiskFreeText));
        Save();
    }

    private void Save()
    {
        if (!_loading)
            Store?.Save(ToSettings());
    }
}

/// <summary>An entry of the Model dropdown.</summary>
public sealed record ModelOption(string Value, string Label)
{
    public override string ToString() => Label;
}
