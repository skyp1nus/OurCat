using OurCut.Core.Editing;
using OurCut.Core.Model;

namespace OurCut.Mcp;

/// <summary>
/// The running editor as the MCP tools see it. <see cref="EditorSession"/> is not thread-safe, so
/// every tool runs its work through <see cref="RunAsync{T}"/>, which the app executes on its UI thread.
/// </summary>
public interface IEditorHost
{
    Task<T> RunAsync<T>(Func<IEditorContext, Task<T>> action);
}

/// <summary>What a tool can read and do inside <see cref="IEditorHost.RunAsync{T}"/>.</summary>
public interface IEditorContext
{
    /// <summary>The project and its undo history. Tools edit through it with <see cref="EditOrigin.Assistant"/>.</summary>
    EditorSession Session { get; }

    /// <summary>A video is open.</summary>
    bool HasFile { get; }

    /// <summary>e.g. "keynote.mp4 · 4K · 29.97 fps"; null without a file.</summary>
    string? SourceSummary { get; }

    /// <summary>Where the project is saved, if it is.</summary>
    string? ProjectPath { get; }

    /// <summary>Playhead position in seconds.</summary>
    double Playhead { get; }

    int? SelectedClipId { get; }
    bool IsPlaying { get; }

    /// <summary>Keyframe times of the source, sorted (empty until scanned).</summary>
    IReadOnlyList<double> Keyframes { get; }

    /// <summary>Silent source ranges, sorted (empty until analysed).</summary>
    IReadOnlyList<TimeRange> Silences { get; }

    /// <summary>Scene change times, sorted (empty until analysed).</summary>
    IReadOnlyList<double> SceneChanges { get; }

    /// <summary>What is still being analysed, e.g. "analysing 45%"; null when done.</summary>
    string? AnalysisStatus { get; }

    /// <summary>Moves the playhead (and the player).</summary>
    void Seek(double time);

    /// <summary>Selects a clip in the timeline and the clip list; null clears the selection.</summary>
    void SelectClip(int? clipId);

    void SetPlaying(bool playing);

    /// <summary>Opens a video or an .ourcut.json project. Returns why it failed, or null.</summary>
    Task<string?> OpenAsync(string path);

    /// <summary>Saves the project (to <paramref name="path"/> if given). Returns why it failed, or null.</summary>
    Task<string?> SaveAsync(string? path);
}
