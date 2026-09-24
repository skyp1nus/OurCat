using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using OurCut.Core.Editing;
using OurCut.Core.Editing.Commands;
using OurCut.Core.Model;
using OurCut.Core.Time;

namespace OurCut.Mcp;

public sealed record ClipInfo(
    int Id,
    [property: Description("1-based position in the output.")] int Position,
    string Label,
    [property: Description("Start in seconds on the source timeline.")] double Start,
    [property: Description("End in seconds on the source timeline.")] double End,
    double Duration,
    [property: Description("False for excluded clips: kept in the project, left out of the export.")] bool Included,
    [property: Description("Start–end as MM:SS.mmm, as the editor shows it.")] string Range);

public sealed record AudioTrackInfo(int Track, string Label);

public sealed record SourceInfo(string Path, string Summary, double Duration, double FrameRate, IReadOnlyList<AudioTrackInfo> AudioTracks);

public sealed record ProjectInfo(
    string Name,
    [property: Description("The open video; null when none is open.")] SourceInfo? Source,
    [property: Description("Where the project is saved, if it is.")] string? ProjectFile,
    double Playhead,
    int? SelectedClip,
    bool Playing,
    [property: Description("Total length of the included clips, in seconds.")] double OutputDuration,
    [property: Description("In output order.")] IReadOnlyList<ClipInfo> Clips,
    [property: Description("Background analysis still running, e.g. \"analysing 45%\".")] string? Analysis);

public sealed record EditResult(
    [property: Description("Id of the edit in the history (for revert_action); null if nothing changed.")] long? Action,
    string Result,
    IReadOnlyList<ClipInfo> Clips,
    double OutputDuration);

public sealed record HistoryItem(long Id, string Description, [property: Description("\"user\" or \"claude\".")] string By, string Time,
    [property: Description("False when undone.")] bool Applied, bool Reverted);

public sealed record VideoFile(string Path, string Name, long SizeBytes, string Modified);

/// <summary>One step of <c>edit_timeline</c>.</summary>
public sealed record EditOperation(
    [property: Description("add, remove, trim, split, include, exclude, move or rename.")] string Action,
    [property: Description("Clip id (all actions except add).")] int? Clip = null,
    [property: Description("Seconds: the range for add, a new start for trim.")] double? Start = null,
    [property: Description("Seconds: the range for add, a new end for trim.")] double? End = null,
    [property: Description("Seconds: where to split.")] double? Time = null,
    [property: Description("Label for add or rename.")] string? Label = null,
    [property: Description("1-based output position for add or move.")] int? Position = null);

/// <summary>
/// The MCP tools: reading the project and editing it through OurCut.Core's commands, so every edit
/// Claude makes is undoable and shows up (highlighted) in the editor's Claude panel.
/// </summary>
public sealed class EditorTools(IEditorHost host)
{
    public const string ServerName = "ourcut";

    public const string Instructions = """
        OurCut is a video editor for cutting long recordings down to the parts worth keeping. One video is
        open at a time. Clips are ranges of that video in seconds on its timeline; their order in the project
        is the order of the output. Parts of the video not covered by an included clip are not exported;
        excluded clips stay in the project but are not exported either.

        Start with get_project. Times are seconds (decimals allowed). Clip ids are stable; positions are
        1-based output positions. Lossless export starts each clip at the keyframe at or before its start;
        use find_keyframes when exact starts matter.

        Everything you change appears in OurCut's Claude panel, highlighted, and the user can undo it. For
        several related changes use edit_timeline with a short description, so they form one undo step.
        """;

    private static readonly string[] VideoExtensions =
        [".mp4", ".mov", ".mkv", ".webm", ".m4v", ".avi", ".ts", ".mts", ".m2ts", ".mpg", ".mpeg", ".flv", ".wmv"];

    // Tool results are read by Claude, not put into HTML: keep "·", "–" and non-English labels as they are.
    internal static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    /// <summary>All tools, bound to <paramref name="host"/>.</summary>
    public static IReadOnlyList<McpServerTool> Create(IEditorHost host)
    {
        var tools = new EditorTools(host);
        return [.. typeof(EditorTools).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .Select(m => McpServerTool.Create(m, m.IsStatic ? null : tools, new McpServerToolCreateOptions { SerializerOptions = Json }))];
    }

    // ---- Reading -------------------------------------------------------------------------

    [McpServerTool(Name = "get_project", Title = "Get the project", ReadOnly = true, Idempotent = true)]
    [Description("The open video, the playhead and every clip in output order. Call this before editing.")]
    public Task<ProjectInfo> GetProject() => host.RunAsync(ctx => Task.FromResult(Describe(ctx)));

    [McpServerTool(Name = "get_history", Title = "List recent edits", ReadOnly = true, Idempotent = true)]
    [Description("Recent edits, newest first, by the user and by you. Ids work with revert_action.")]
    public Task<IReadOnlyList<HistoryItem>> GetHistory([Description("How many edits (default 30).")] int limit = 30) =>
        host.RunAsync(ctx =>
        {
            var session = ctx.Session;
            IReadOnlyList<HistoryItem> items = [.. session.History.Entries.Reverse().Take(Math.Clamp(limit, 1, 500))
                .Select(e => new HistoryItem(e.Id, e.Description, e.Origin == EditOrigin.Assistant ? "claude" : "user",
                    e.Time.ToString("HH:mm:ss", CultureInfo.InvariantCulture), session.History.IsApplied(e), session.IsReverted(e)))];
            return Task.FromResult(items);
        });

    [McpServerTool(Name = "find_keyframes", Title = "Find keyframes", ReadOnly = true, Idempotent = true)]
    [Description("Keyframe times between two points of the source. A lossless export starts each clip at the keyframe at or " +
                 "before its start, so starting clips on keyframes avoids extra lead-in.")]
    public Task<IReadOnlyList<double>> FindKeyframes([Description("Seconds.")] double start, [Description("Seconds.")] double end) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            if (ctx.Keyframes.Count == 0)
                throw new McpException("Keyframes are not scanned yet" + (ctx.AnalysisStatus is { } a ? $" ({a})" : "") + ". Try again shortly.");
            IReadOnlyList<double> times = [.. ctx.Keyframes.Where(t => t >= start && t <= end).Take(500).Select(Round)];
            return Task.FromResult(times);
        });

    [McpServerTool(Name = "list_videos", Title = "List recent videos", ReadOnly = true, Idempotent = true, OpenWorld = true)]
    [Description("Video files in a folder (default: the user's Videos folder) and its subfolders, newest first. " +
                 "Use a path from here with open_file.")]
    public static IReadOnlyList<VideoFile> ListVideos(
        [Description("Folder to search; the user's Videos folder if omitted.")] string? folder = null,
        [Description("How many files (default 20).")] int limit = 20)
    {
        folder ??= DefaultVideosFolder();
        RequireFullPath(folder);
        if (!Directory.Exists(folder))
            throw new McpException($"The folder {folder} does not exist.");
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        return [.. new DirectoryInfo(folder).EnumerateFiles("*", options)
            .Where(f => VideoExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(Math.Clamp(limit, 1, 200))
            .Select(f => new VideoFile(f.FullName, f.Name, f.Length, f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))];
    }

    // ---- Editing -------------------------------------------------------------------------

    [McpServerTool(Name = "add_segment", Title = "Keep a range")]
    [Description("Keeps a range of the source as a new clip. Returns the updated clip list; the new clip is the one with the highest id.")]
    public Task<EditResult> AddSegment(
        [Description("Seconds.")] double start,
        [Description("Seconds.")] double end,
        [Description("Short name shown in the clip list.")] string? label = null,
        [Description("1-based output position; the end if omitted.")] int? position = null) =>
        Edit(_ => new AddClipCommand(start, end, label, position - 1));

    [McpServerTool(Name = "remove_segment", Title = "Remove a clip", Destructive = true)]
    [Description("Removes a clip from the project. To only leave it out of the export, use set_included instead.")]
    public Task<EditResult> RemoveSegment(int clip) => Edit(_ => new RemoveClipCommand(clip));

    [McpServerTool(Name = "trim_segment", Title = "Trim a clip")]
    [Description("Moves a clip's start and/or end.")]
    public Task<EditResult> TrimSegment(int clip, [Description("New start, seconds.")] double? start = null,
        [Description("New end, seconds.")] double? end = null) =>
        Edit(_ => new PartialTrimCommand(clip, start, end));

    [McpServerTool(Name = "split_segment", Title = "Split a clip")]
    [Description("Splits a clip in two at a source time; the second part gets a new id.")]
    public Task<EditResult> SplitSegment(int clip, [Description("Seconds, inside the clip.")] double time) =>
        Edit(_ => new SplitClipCommand(clip, time));

    [McpServerTool(Name = "set_included", Title = "Exclude or keep a clip")]
    [Description("Excludes a clip from the export (it stays in the project) or includes it again.")]
    public Task<EditResult> SetIncluded(int clip, bool included) => Edit(_ => new SetClipIncludedCommand(clip, included));

    [McpServerTool(Name = "move_segment", Title = "Reorder a clip")]
    [Description("Moves a clip to another position in the output.")]
    public Task<EditResult> MoveSegment(int clip, [Description("1-based output position.")] int position) =>
        Edit(_ => new MoveClipCommand(clip, position - 1));

    [McpServerTool(Name = "set_label", Title = "Rename a clip")]
    [Description("Renames a clip. Labels become chapter titles and file names on export.")]
    public Task<EditResult> SetLabel(int clip, string label) => Edit(_ => new RenameClipCommand(clip, label));

    [McpServerTool(Name = "edit_timeline", Title = "Make several edits")]
    [Description("Applies several edits in order as one undo step, e.g. keeping a list of ranges or excluding several clips. " +
                 "If one fails, none is applied.")]
    public Task<EditResult> EditTimeline(
        IReadOnlyList<EditOperation> operations,
        [Description("What the edits do, in a few words; shown to the user, e.g. \"Kept the three demo segments\".")] string description) =>
        Edit(_ =>
        {
            if (operations.Count == 0)
                throw new EditException("No operations given.");
            return new BatchCommand("edit_timeline", string.IsNullOrWhiteSpace(description) ? $"{operations.Count} edits" : description.Trim(),
                [.. operations.Select(ToCommand)]);
        });

    [McpServerTool(Name = "revert_action", Title = "Revert an edit")]
    [Description("Reverts one earlier edit (an id from get_history or an edit result) and keeps everything done after it.")]
    public Task<EditResult> RevertAction([Description("Edit id.")] long action) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var entry = ctx.Session.History.Find(action) ?? throw new McpException($"There is no edit {action} in the history.");
            var reverted = Guard(() => ctx.Session.Revert(entry, EditOrigin.Assistant));
            return Task.FromResult(Result(ctx, reverted));
        });

    [McpServerTool(Name = "undo", Title = "Undo")]
    [Description("Undoes the last edit, whoever made it.")]
    public Task<EditResult> Undo() => UndoRedo(undo: true);

    [McpServerTool(Name = "redo", Title = "Redo")]
    [Description("Redoes the last undone edit.")]
    public Task<EditResult> Redo() => UndoRedo(undo: false);

    // ---- Player and files ----------------------------------------------------------------

    [McpServerTool(Name = "seek", Title = "Move the playhead")]
    [Description("Moves the playhead so the user sees that frame; with a clip, also selects it (and goes to its start if no time is given).")]
    public Task<string> Seek([Description("Seconds.")] double? time = null, [Description("Clip id to select.")] int? clip = null) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            if (clip is { } id)
            {
                var c = ctx.Session.Project.Find(id) ?? throw new McpException($"Clip {id} does not exist.");
                ctx.SelectClip(id);
                time ??= c.Start;
            }
            if (time is { } t)
                ctx.Seek(Math.Clamp(t, 0, ctx.Session.Project.SourceDuration));
            return Task.FromResult($"Playhead at {TimeFormat.Timecode(ctx.Playhead)}.");
        });

    [McpServerTool(Name = "set_playing", Title = "Play or pause")]
    [Description("Starts or pauses playback in OurCut.")]
    public Task<string> SetPlaying(bool playing) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            ctx.SetPlaying(playing);
            return Task.FromResult(playing ? "Playing." : "Paused.");
        });

    [McpServerTool(Name = "open_file", Title = "Open a video or project", OpenWorld = true)]
    [Description("Opens a video (as a new, empty project) or a saved .ourcut.json project in OurCut, replacing what is open.")]
    public Task<ProjectInfo> OpenFile([Description("Full path of the file.")] string path) =>
        host.RunAsync(async ctx =>
        {
            RequireFullPath(path);
            if (!File.Exists(path))
                throw new McpException($"{path} does not exist.");
            if (await ctx.OpenAsync(path).ConfigureAwait(true) is { } error)
                throw new McpException(error);
            return Describe(ctx);
        });

    [McpServerTool(Name = "save_project", Title = "Save the project")]
    [Description("Saves the project as .ourcut.json: where it was saved before, or to the given path.")]
    public Task<string> SaveProject([Description("Full path ending in .ourcut.json; needed the first time.")] string? path = null) =>
        host.RunAsync(async ctx =>
        {
            RequireFile(ctx);
            if (path is null && ctx.ProjectPath is null)
                throw new McpException("The project has not been saved yet; give a path ending in .ourcut.json.");
            if (path is not null)
                RequireFullPath(path);
            if (await ctx.SaveAsync(path).ConfigureAwait(true) is { } error)
                throw new McpException(error);
            return $"Saved to {ctx.ProjectPath}.";
        });

    // ---- Helpers -------------------------------------------------------------------------

    private Task<EditResult> Edit(Func<Project, IEditCommand> command) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var entry = Guard(() => ctx.Session.Execute(command(ctx.Session.Project), EditOrigin.Assistant));
            return Task.FromResult(Result(ctx, entry));
        });

    private Task<EditResult> UndoRedo(bool undo) =>
        host.RunAsync(ctx =>
        {
            RequireFile(ctx);
            var history = ctx.Session.History;
            var entry = undo ? history.NextUndo : history.NextRedo;
            if (entry is null)
                throw new McpException(undo ? "There is nothing to undo." : "There is nothing to redo.");
            _ = undo ? ctx.Session.Undo() : ctx.Session.Redo();
            var project = ctx.Session.Project;
            return Task.FromResult(new EditResult(null, $"{(undo ? "Undid" : "Redid")} “{entry.Description}”.", Clips(project),
                Round(project.OutputDuration)));
        });

    /// <summary>Edit errors ("Clip 7 does not exist") go back to Claude as tool errors it can act on.</summary>
    private static T Guard<T>(Func<T> edit)
    {
        try
        {
            return edit();
        }
        catch (EditException e)
        {
            throw new McpException(e.Message);
        }
    }

    private static void RequireFile(IEditorContext ctx)
    {
        if (!ctx.HasFile)
            throw new McpException("No video is open in OurCut. Open one with open_file (list_videos finds recent ones).");
    }

    /// <summary>OurCut runs in its own folder, so a relative path would not mean what Claude meant.</summary>
    private static void RequireFullPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new McpException($"“{path}” is not a full path; give the whole path, e.g. from list_videos.");
    }

    private static IEditCommand ToCommand(EditOperation op)
    {
        int Clip() => op.Clip ?? throw new EditException($"“{op.Action}” needs a clip id.");
        double Need(double? value, string name) => value ?? throw new EditException($"“{op.Action}” needs {name}.");
        return op.Action.Trim().ToLowerInvariant() switch
        {
            "add" => new AddClipCommand(Need(op.Start, "start"), Need(op.End, "end"), op.Label, op.Position - 1),
            "remove" => new RemoveClipCommand(Clip()),
            "trim" => new PartialTrimCommand(Clip(), op.Start, op.End),
            "split" => new SplitClipCommand(Clip(), Need(op.Time, "time")),
            "include" => new SetClipIncludedCommand(Clip(), true),
            "exclude" => new SetClipIncludedCommand(Clip(), false),
            "move" => new MoveClipCommand(Clip(), (op.Position ?? throw new EditException("“move” needs a position.")) - 1),
            "rename" => new RenameClipCommand(Clip(), op.Label ?? throw new EditException("“rename” needs a label.")),
            _ => throw new EditException($"Unknown action “{op.Action}”; use add, remove, trim, split, include, exclude, move or rename."),
        };
    }

    private static EditResult Result(IEditorContext ctx, HistoryEntry? entry)
    {
        var project = ctx.Session.Project;
        return new EditResult(entry?.Id, entry?.Description ?? "Nothing changed.", Clips(project), Round(project.OutputDuration));
    }

    internal static ProjectInfo Describe(IEditorContext ctx)
    {
        var project = ctx.Session.Project;
        var source = ctx.HasFile && project.Source is { } s
            ? new SourceInfo(s.Path, ctx.SourceSummary ?? Path.GetFileName(s.Path), Round(s.Duration), Math.Round(s.FrameRate, 3),
                [.. s.AudioTracks.Select((t, i) => new AudioTrackInfo(i + 1, t.Label))])
            : null;
        return new ProjectInfo(project.Name, source, ctx.ProjectPath, Round(ctx.Playhead), ctx.SelectedClipId, ctx.IsPlaying,
            Round(project.OutputDuration), source is null ? [] : Clips(project), ctx.AnalysisStatus);
    }

    private static List<ClipInfo> Clips(Project project) =>
        [.. project.Clips.Select((c, i) => new ClipInfo(c.Id, i + 1, c.Label, Round(c.Start), Round(c.End), Round(c.Duration), c.IsIncluded,
            $"{TimeFormat.MinutesSeconds(c.Start)}–{TimeFormat.MinutesSeconds(c.End)}"))];

    private static double Round(double seconds) => Math.Round(seconds, 3);

    private static string DefaultVideosFolder()
    {
        string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        return videos.Length > 0 ? videos : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Videos");
    }
}

/// <summary>Moves one or both ends of a clip; the other stays where it is when the edit is applied.</summary>
internal sealed record PartialTrimCommand(int ClipId, double? Start, double? End) : IEditCommand
{
    public string Name => "trim_segment";

    public string Describe(Project before) => Resolve(before).Describe(before);

    public Project Apply(Project project) => Resolve(project).Apply(project);

    private SetClipRangeCommand Resolve(Project project)
    {
        if (Start is null && End is null)
            throw new EditException("Give a new start, a new end or both.");
        var clip = project.Get(ClipId);
        return new SetClipRangeCommand(ClipId, Start ?? clip.Start, End ?? clip.End);
    }
}
