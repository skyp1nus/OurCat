using Avalonia.Threading;
using OurCut.App.ViewModels;
using OurCut.Core.Editing;
using OurCut.Media.Export;
using OurCut.Mcp;

namespace OurCut.App.Services;

/// <summary>
/// The editor as the MCP tools see it. Tool calls arrive on pipe threads; each one runs on the UI
/// thread, where the session and the view models live, so an edit by Claude never lands in the
/// middle of one by the user. Claude's edits then show up like the user's (and highlighted) in the
/// timeline, the clip list and the Claude panel.
/// </summary>
public sealed class EditorMcpHost(EditorViewModel editor) : IEditorHost, IEditorContext
{
    public Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action) =>
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            editor.Claude.NoteActivity();
            return action(this);
        });

    public EditorSession Session => editor.Session;
    public bool HasFile => editor.HasFile;
    public string? SourceSummary => editor.HasFile ? editor.MediaInfoText : null;
    public string? ProjectPath => editor.ProjectPath;
    public double Playhead => editor.Time;
    public int? SelectedClipId => editor.SelectedClip?.Id;
    public bool IsPlaying => editor.IsPlaying;
    public IReadOnlyList<double> Keyframes => editor.Media?.Keyframes ?? [];

    public SilenceReport? FindSilences(double minDuration, double? thresholdDb, IReadOnlyList<int>? streams) =>
        editor.Media?.FindSilences(minDuration, thresholdDb, streams) is { } found
            ? new SilenceReport(found.Ranges, found.ThresholdDb, found.NoiseFloorDb, found.IsComplete)
            : null;

    public SceneReport? FindSceneChanges(double threshold) =>
        editor.Media?.FindSceneChanges(threshold) is { } found
            ? new SceneReport(found.Changes, found.IsComplete, found.Progress)
            : null;
    public string? AnalysisStatus => editor.Media?.Activity;

    public string? StartExport(ExportRequest request) =>
        editor.Export.StartForClaude(
            request.Mode switch { "lossless" => ExportMode.Copy, "reencode" => ExportMode.Encode, _ => null },
            request.Container?.ToUpperInvariant(),
            request.Merge,
            request.Folder,
            request.Chapters,
            request.AllTracks,
            request.Video switch
            {
                "h264" => VideoEncoding.H264Quality,
                "h264_fast" => VideoEncoding.H264Fast,
                "h265" => VideoEncoding.H265,
                _ => null,
            },
            request.Audio is null ? null : request.Audio == "copy");

    public ExportState? Export => editor.Export switch
    {
        { Outcome: ExportOutcome.None } => null,
        var e => new ExportState(e.Outcome.ToString().ToLowerInvariant(), e.Outcome == ExportOutcome.Done ? 1 : e.Progress,
            e.OutputFiles, e.Outcome == ExportOutcome.Failed ? e.ErrorText : null, e.Footer),
    };

    public bool CancelExport()
    {
        if (editor.Export.Outcome != ExportOutcome.Running)
            return false;
        editor.Export.Close();
        return true;
    }

    public void Seek(double time) => editor.SetTime(time);

    public void SelectClip(int? clipId) => editor.Select(clipId is { } id ? editor.Find(id) : null);

    public void SetPlaying(bool playing)
    {
        if (playing != editor.IsPlaying)
            editor.TogglePlay();
    }

    public Task<string?> OpenAsync(string path) => editor.OpenForClaudeAsync(path);

    public Task<string?> SaveAsync(string? path) => editor.SaveForClaudeAsync(path);
}

/// <summary>Runs the editor's MCP server and shows its state in the title bar and the Claude panel.</summary>
public sealed class EditorMcpServer : IAsyncDisposable
{
    private readonly McpPipeServer _server;
    private readonly EditorViewModel _editor;

    public EditorMcpServer(EditorViewModel editor, string? pipeName = null)
    {
        _editor = editor;
        _server = new McpPipeServer(new EditorMcpHost(editor), pipeName);
        _server.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    public string PipeName => _server.PipeName;

    public void Start()
    {
        _server.Start();
        Refresh();
    }

    private void Refresh()
    {
        _editor.Claude.IsListening = _server.IsListening;
        _editor.Claude.IsServedElsewhere = _server.IsInUseElsewhere;
        _editor.Claude.IsConnected = _server.Sessions > 0;
    }

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}
