using Avalonia.Threading;
using OurCut.App.ViewModels;
using OurCut.Core.Editing;
using OurCut.Media.Export;
using OurCut.Mcp;
using Fillers = OurCut.Core.Transcripts.FillerWords;

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

    // STUB: expose through IEditorContext so find/cut_filler_words default to the user's list.
    /// <summary>Settings → Transcription → Filler words, every language.</summary>
    public IReadOnlyList<string> FillerWords => Fillers.All(editor.Settings.FillerWords);

    public TranscriptStatus TranscriptStatus => editor.Media is { } media
        ? new TranscriptStatus(media.TranscriptState.ToString().ToLowerInvariant(), media.TranscriptProgress, media.Transcript, media.TranscriptError)
        : new TranscriptStatus("none", 0, null, null);

    public string? StartTranscription() => editor.StartTranscription();

    public async Task<string?> StartExportAsync(ExportRequest request, CancellationToken cancellationToken)
    {
        var export = editor.Export;
        var claude = editor.Claude.Export;
        if (claude.IsAsking)
            return "An export is already waiting for the user's answer.";
        if (export.PrepareForClaude(
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
                request.Audio is null ? null : request.Audio == "copy") is { } error)
            return error;
        bool started = false;
        try
        {
            if (await AskToExportAsync(export.ClaudeTarget(), cancellationToken).ConfigureAwait(true) == ClaudeExportAnswer.Deny)
                return "The user declined the export.";
            if (export.StartPreparedForClaude() is { } refused)
            {
                claude.Clear();
                return refused;
            }
            started = true;
            return null;
        }
        catch (OperationCanceledException)
        {
            return "The export request was withdrawn.";
        }
        finally
        {
            if (!started)
                export.AbandonPreparedForClaude();
        }
    }

    // STUB: pick by editor.Settings.ExportPermission: Allow → Allow, Ask → editor.Claude.Export.AskAsync(target, ct), Never → refuse with its own message.
    private static Task<ClaudeExportAnswer> AskToExportAsync(ClaudeExportTarget target, CancellationToken cancellationToken) =>
        Task.FromResult(ClaudeExportAnswer.Allow);

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
        editor.Export.CancelExport();
        return true;
    }

    public void Seek(double time) => editor.SetTime(time);

    public void SelectClip(int? clipId) => editor.Select(clipId is { } id ? editor.Find(id) : null);

    public void SetPlaying(bool playing)
    {
        if (playing != editor.IsPlaying)
            editor.TogglePlay();
    }

    // STUB: honour Settings.OpenFilesPermission; Ask needs a request like the export one.
    public Task<string?> OpenAsync(string path) => editor.OpenForClaudeAsync(path);

    // STUB: honour Settings.SaveProjectPermission; Ask needs a request like the export one.
    public Task<string?> SaveAsync(string? path) => editor.SaveForClaudeAsync(path);
}
