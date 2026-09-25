using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OurCut.App.Services;
using OurCut.Media;
using OurCut.Media.Caching;

namespace OurCut.App.ViewModels;

// Settings → General: startup, autosave, recent files, cache, versions and support.
public sealed partial class SettingsViewModel
{
    private DispatcherTimer? _copiedTimer;
    private bool _versionsRead;

    private ChoiceSet<StartupAction>? _startupChoices;
    private ChoiceSet<StartupAction> StartupChoices => _startupChoices ??= new(
        [(StartupAction.OpenLastProject, "Open the last project"), (StartupAction.StartEmpty, "Start empty")], v => Startup = v, Startup);
    public IReadOnlyList<ChoiceOption> StartupOptions => StartupChoices.Options;

    private ChoiceSet<int>? _recentLimitChoices;
    private ChoiceSet<int> RecentLimitChoices => _recentLimitChoices ??= new(
        [(5, "5"), (10, "10"), (20, "20")], v => RecentFilesLimit = v, RecentFilesLimit);
    public IReadOnlyList<ChoiceOption> RecentLimitOptions => RecentLimitChoices.Options;

    [ObservableProperty]
    public partial StartupAction Startup { get; set; } = StartupAction.OpenLastProject;

    [ObservableProperty]
    public partial bool Autosave { get; set; } = true;

    [ObservableProperty]
    public partial int RecentFilesLimit { get; set; } = 10;

    /// <summary>"Clear list" was clicked (the link then says so).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecentLinkText))]
    public partial bool IsRecentListCleared { get; set; }

    public string RecentLinkText => IsRecentListCleared ? "List cleared" : "Clear list";

    [ObservableProperty]
    public partial string CacheFolder { get; set; } = MediaCache.DefaultRoot;

    /// <summary>"1.8 GB · 42 videos"; "—" until it can be measured.</summary>
    [ObservableProperty]
    public partial string CacheUsedText { get; set; } = "—";

    [ObservableProperty]
    public partial bool CanClearCache { get; set; } = true;

    [ObservableProperty]
    public partial string AppVersion { get; set; } = ReadAppVersion();

    [ObservableProperty]
    public partial string FfmpegVersion { get; set; } = "…";

    [ObservableProperty]
    public partial string LibmpvVersion { get; set; } = "…";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DiagnosticsButtonText))]
    public partial bool DiagnosticsCopied { get; set; }

    public string DiagnosticsButtonText => DiagnosticsCopied ? "Copied" : "Copy diagnostics";

    partial void OnStartupChanged(StartupAction value)
    {
        _startupChoices?.Select(value);
        // STUB: App start does not read Startup yet; OpenLastProject should open RecentFiles[0] when no file was passed.
        SaveGeneral();
    }

    partial void OnAutosaveChanged(bool value)
    {
        _editor.AutosaveEnabled = value;
        SaveGeneral();
    }

    partial void OnRecentFilesLimitChanged(int value)
    {
        _recentLimitChoices?.Select(value);
        // STUB: RecentFilesStore still keeps RecentFilesStore.Capacity (8) entries; make it take this limit.
        SaveGeneral();
    }

    private void SaveGeneral() => UpdateSettings(s => s with { General = ToGeneralSettings() });

    internal GeneralSettings ToGeneralSettings() => new(Startup, Autosave, RecentFilesLimit);

    /// <summary>Shows <paramref name="s"/>; a value the dialog does not offer reads as its default.</summary>
    internal void LoadGeneral(GeneralSettings? s)
    {
        s ??= new();
        Startup = Enum.IsDefined(s.Startup) ? s.Startup : StartupAction.OpenLastProject;
        Autosave = s.Autosave;
        RecentFilesLimit = s.RecentFilesLimit is 5 or 10 or 20 ? s.RecentFilesLimit : 10;
        _editor.AutosaveEnabled = Autosave;
    }

    [RelayCommand]
    private void ClearRecentFiles()
    {
        _editor.RecentStore?.Clear();
        _editor.RecentFiles.Clear();
        IsRecentListCleared = true;
    }

    [RelayCommand]
    private void OpenCacheFolder()
    {
        if (!_editor.IsDemo)
            Reveal(CacheFolder);
    }

    [RelayCommand]
    private void ClearCache()
    {
        if (_editor.IsDemo)
        {
            CacheUsedText = "0 B · 0 videos";
            CanClearCache = false;
            return;
        }
        // STUB: delete the cache folders except the open video's and every transcript-*.json (slow to redo), then measure again.
    }

    [RelayCommand]
    private void OpenLogsFolder()
    {
        if (!_editor.IsDemo)
            Reveal(CrashLog.Folder);
    }

    private static void Reveal(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            FileManager.Reveal(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing to show.
        }
    }

    /// <summary>Copies versions and paths for a bug report; the button says "Copied" for 1.5 s.</summary>
    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        await (CopyText?.Invoke(DiagnosticsText()) ?? Task.CompletedTask).ConfigureAwait(true);
        DiagnosticsCopied = true;
        _copiedTimer?.Stop();
        _copiedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, (_, _) =>
        {
            _copiedTimer?.Stop();
            _copiedTimer = null;
            DiagnosticsCopied = false;
        });
        _copiedTimer.Start();
    }

    internal string DiagnosticsText() => string.Join(Environment.NewLine,
        $"OurCut {AppVersion}",
        $"ffmpeg {FfmpegVersion}",
        $"libmpv {LibmpvVersion}",
        $"{RuntimeInformation.OSDescription} · {RuntimeInformation.OSArchitecture}",
        RuntimeInformation.FrameworkDescription,
        $"Hardware decoding {HardwareDecoding} · renderer {Renderer}",
        $"Settings {AppSettingsStore.DefaultFile}",
        $"Logs {CrashLog.Folder}");

    /// <summary>"1.8 GB · 42 videos" (SI units, like the design).</summary>
    internal static string FormatCacheUsage(long bytes, int videos)
    {
        var c = CultureInfo.InvariantCulture;
        string size = bytes switch
        {
            < 1_000 => bytes.ToString(c) + " B",
            < 1_000_000 => (bytes / 1e3).ToString("0", c) + " KB",
            < 1_000_000_000 => (bytes / 1e6).ToString("0", c) + " MB",
            _ => (bytes / 1e9).ToString("0.0", c) + " GB",
        };
        return $"{size} · {videos.ToString(c)} {(videos == 1 ? "video" : "videos")}";
    }

    // STUB: sum the file sizes under root; each sub-folder is one video.
    private static (long Bytes, int Videos)? MeasureCache(string root) => null;

    /// <summary>Measures the cache and reads the tool versions (once); the demo keeps its sample values.</summary>
    private void RefreshGeneral()
    {
        if (_editor.IsDemo)
            return;
        CacheFolder = MediaCache.DefaultRoot;
        var used = MeasureCache(CacheFolder);
        CacheUsedText = used is { } u ? FormatCacheUsage(u.Bytes, u.Videos) : "—";
        CanClearCache = used is not { Bytes: 0 };
        if (_versionsRead)
            return;
        _versionsRead = true;
        _ = ReadVersionsAsync();
    }

    private async Task ReadVersionsAsync()
    {
        string? ffmpeg = await Task.Run(() => NativeTools.GetVersionAsync("ffmpeg")).ConfigureAwait(true);
        FfmpegVersion = ffmpeg ?? "not found";
        var mpv = (_editor.Player as MpvPlaybackEngine)?.Mpv;
        string? libmpv = mpv is null ? null : await Task.Run(() => mpv.GetPropertyString("mpv-version")).ConfigureAwait(true);
        LibmpvVersion = MpvVersion(libmpv) ?? "not available";
    }

    /// <summary>"mpv v0.39.0-dirty (…)" → "0.39.0-dirty".</summary>
    internal static string? MpvVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        string v = text.Trim();
        if (v.StartsWith("mpv ", StringComparison.OrdinalIgnoreCase))
            v = v[4..].TrimStart();
        if (v.StartsWith('v'))
            v = v[1..];
        int space = v.IndexOf(' ', StringComparison.Ordinal);
        return space > 0 ? v[..space] : v;
    }

    private static string ReadAppVersion()
    {
        string? version = typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return version?.Split('+')[0] ?? "—";
    }
}
