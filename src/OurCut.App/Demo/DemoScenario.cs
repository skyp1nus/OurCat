using OurCut.App.ViewModels;
using OurCut.Core.Editing;
using OurCut.Core.Editing.Commands;
using OurCut.Core.Model;

namespace OurCut.App.Demo;

/// <summary>
/// Puts the editor into one of the design's screens with the prototype's sample data
/// (<c>fresh()</c>, <c>baseClips()</c>, <c>editLog()</c> and <c>aiLog()</c> in the design file).
/// Claude's scripted actions are real Core commands made with <see cref="EditOrigin.Assistant"/>.
/// </summary>
public static class DemoScenario
{
    public static void Apply(EditorViewModel editor, DesignScreen screen)
    {
        editor.IsDemo = true;
        editor.Export.Close();
        editor.Unload();
        editor.PlaceholderDuration = DesignSample.SampleDuration;
        editor.Claude.Log.Clear();
        editor.Claude.IsConnected = true;
        editor.Claude.IsOpen = true;
        editor.RecentFiles.Clear();
        editor.RecentFiles.Add(new("keynote_final_4k.mp4", "", "14:32", "Yesterday"));
        editor.RecentFiles.Add(new("podcast_ep41_raw.mov", "", "1:12:08", "Sep 19"));
        editor.RecentFiles.Add(new("drone_coast_0412.mp4", "", "6:40", "Sep 14"));
        editor.ToolStatus = "ffmpeg 7.1 · ready";
        editor.ZoomLevel = 0;
        editor.Time = 301.42;

        if (screen == DesignScreen.Empty)
        {
            editor.Claude.Recount();
            return;
        }

        var project = DesignSample.Project;
        if (screen == DesignScreen.Ai)
        {
            // Claude's two trims from the "ai" screen are already applied.
            project = new SetClipRangeCommand(2, 123.14, 190.12).Apply(project);
            project = new SetClipRangeCommand(5, 750, 812.4).Apply(project);
        }
        editor.LoadProject(project, new DesignSample(), DesignSample.SourceInfo);
        AddEditLog(editor);

        if (screen == DesignScreen.Ai)
        {
            AddAiLog(editor, "Yes, cut it. Also tighten the setup and the outro.");
            editor.Time = 541.2;
            editor.Select(editor.Find(4));
        }
        else
        {
            editor.Time = 301.42;
            editor.Select(editor.Find(3));
        }
        editor.Claude.Recount();
        editor.ApplyClaudeHighlights();

        if (screen == DesignScreen.Export)
        {
            editor.Export.Open();
        }
        else if (screen == DesignScreen.Exporting)
        {
            editor.Export.Loop = true;
            editor.Export.Start(0.42);
        }
    }

    /// <summary>Opens the sample project as a normal (non-demo) project.</summary>
    public static void OpenSample(EditorViewModel editor)
    {
        editor.LeaveDemo();
        editor.LoadProject(DesignSample.Project, new DesignSample(), DesignSample.SourceInfo);
    }

    private static void AddEditLog(EditorViewModel editor)
    {
        var claude = editor.Claude;
        var session = editor.Session;
        string[] names = ["Intro", "Setup", "Demo — import", "Demo — trim", "Outro"];

        claude.Add(new(ClaudeLogKind.User, "Cut this down to the intro, setup, both demo parts and the outro. Drop the dead air."));
        claude.Add(new(ClaudeLogKind.Action, "Detected 8 scene changes", "detect_scenes · 0.4 s"));

        List<(Clip Clip, int Index)> removed = [];
        claude.Add(new(ClaudeLogKind.Action, "Added 5 clips at scene boundaries", "add_segment ×5", setUndone: undone =>
        {
            if (undone)
            {
                removed = [.. session.Project.Clips.Select((c, i) => (c, i)).Where(x => x.c.Id is >= 1 and <= 5)];
                session.Execute(new BatchCommand("remove_segment", "Removed 5 clips",
                    [.. removed.Select(x => (IEditCommand)new RemoveClipCommand(x.Clip.Id))]), EditOrigin.Assistant);
            }
            else
            {
                session.Execute(new BatchCommand("add_segment", "Added 5 clips at scene boundaries",
                    [.. removed.Select(x => (IEditCommand)new AddClipCommand(x.Clip.Start, x.Clip.End, x.Clip.Label,
                        Math.Min(x.Index, session.Project.Clips.Count), x.Clip.Id, x.Clip.IsIncluded))]), EditOrigin.Assistant);
            }
        }));
        claude.Add(new(ClaudeLogKind.Action, "Removed silence 3×", "trim_segment ×3 · −15.34 s", setUndone: undone =>
        {
            var p = session.Project;
            session.Execute(new BatchCommand("trim_segment", "Removed silence 3×",
            [
                new SetClipRangeCommand(1, undone ? 8.1 : 12.04, p.Get(1).End),
                new SetClipRangeCommand(3, p.Get(3).Start, undone ? 410.6 : 404),
                new SetClipRangeCommand(5, undone ? 745.2 : 750, p.Get(5).End),
            ]), EditOrigin.Assistant);
        }));
        claude.Add(new(ClaudeLogKind.Action, "Labeled clips from transcript", "set_label ×5", setUndone: undone =>
            session.Execute(new BatchCommand("set_label", "Labeled clips from transcript",
                [.. Enumerable.Range(1, 5).Select(id => (IEditCommand)new RenameClipCommand(id, undone ? "Segment " + id : names[id - 1]))]),
                EditOrigin.Assistant)));
        claude.Add(new(ClaudeLogKind.Claude, "Output is 7:32.000, under your 8 minute target. Clip 4 has a 6 s pause around 00:09:00. Want me to cut it?"));
    }

    private static void AddAiLog(EditorViewModel editor, string text)
    {
        var claude = editor.Claude;
        var session = editor.Session;
        claude.Add(new(ClaudeLogKind.User, text));
        claude.Add(new(ClaudeLogKind.Action, "Trimmed clip 2 in-point", "trim_segment · +4.54 s", [2], -4.54, changed: true,
            setUndone: undone => session.SetRange(2, undone ? 118.6 : 123.14, session.Project.Get(2).End, EditOrigin.Assistant)));
        claude.Add(new(ClaudeLogKind.Action, "Trimmed clip 5 out-point", "trim_segment · −16.32 s", [5], -16.32, changed: true,
            setUndone: undone => session.SetRange(5, session.Project.Get(5).Start, undone ? 828.72 : 812.4, EditOrigin.Assistant)));
        claude.Add(new(ClaudeLogKind.Live, "Removing pause in clip 4", "detect_silence · 08:15 – 10:02", [4]));
    }
}
