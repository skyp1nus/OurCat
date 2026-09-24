using OurCut.App.ViewModels;

namespace OurCut.App.Demo;

/// <summary>
/// Puts the editor into one of the design's screens with the prototype's sample data
/// (<c>fresh()</c>, <c>baseClips()</c>, <c>editLog()</c> and <c>aiLog()</c> in the design file).
/// </summary>
public static class DemoScenario
{
    public static void Apply(EditorViewModel editor, DesignScreen screen)
    {
        var sample = new DesignSample();
        editor.Export.Close();
        editor.Unload();
        editor.PlaceholderDuration = sample.Duration;
        editor.Claude.Log.Clear();
        editor.Claude.IsConnected = true;
        editor.Claude.IsOpen = true;
        editor.RecentFiles.Clear();
        editor.RecentFiles.Add(new("keynote_final_4k.mp4", "", "14:32", "Yesterday"));
        editor.RecentFiles.Add(new("podcast_ep41_raw.mov", "", "1:12:08", "Sep 19"));
        editor.RecentFiles.Add(new("drone_coast_0412.mp4", "", "6:40", "Sep 14"));
        editor.Time = 301.42;
        editor.ToolStatus = "ffmpeg 7.1 · ready";
        editor.ZoomLevel = 0;

        if (screen == DesignScreen.Empty)
        {
            editor.Claude.Recount();
            return;
        }

        editor.LoadMedia(sample, "launch-keynote", "keynote_final_4k.mp4", "keynote_final_4k.mp4 · 4K · 29.97 fps",
            DesignSample.AudioStreams);
        foreach (var c in BaseClips())
            editor.Clips.Add(c);
        AddEditLog(editor);

        if (screen == DesignScreen.Ai)
        {
            AddAiLog(editor, "Yes, cut it. Also tighten the setup and the outro.");
            editor.Time = 541.2;
            editor.Select(editor.Clips.First(c => c.Id == 4));
        }
        else
        {
            editor.Select(editor.Clips.First(c => c.Id == 3));
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

    private static IEnumerable<ClipViewModel> BaseClips() =>
    [
        new(1, "Intro", 12.04, 45.32),
        new(2, "Setup", 118.6, 190.12),
        new(3, "Demo — import", 242.88, 404.0),
        new(4, "Demo — trim", 495.2, 602.56),
        new(6, "Q&A", 640.0, 728.4, isIncluded: false),
        new(5, "Outro", 750.0, 828.72),
    ];

    private static void AddEditLog(EditorViewModel editor)
    {
        var claude = editor.Claude;
        ClipViewModel Clip(int id) => editor.Clips.First(c => c.Id == id);
        string[] names = ["Intro", "Setup", "Demo — import", "Demo — trim", "Outro"];

        claude.Add(new(ClaudeLogKind.User, "Cut this down to the intro, setup, both demo parts and the outro. Drop the dead air."));
        claude.Add(new(ClaudeLogKind.Action, "Detected 8 scene changes", "detect_scenes · 0.4 s"));

        List<(ClipViewModel Clip, int Index)> removed = [];
        claude.Add(new(ClaudeLogKind.Action, "Added 5 clips at scene boundaries", "add_segment ×5", setUndone: undone =>
        {
            if (undone)
            {
                removed = [.. editor.Clips.Select((c, i) => (c, i)).Where(x => x.c.Id is >= 1 and <= 5)];
                foreach (var (c, _) in removed)
                    editor.Clips.Remove(c);
            }
            else
            {
                foreach (var (c, i) in removed)
                    editor.Clips.Insert(Math.Min(i, editor.Clips.Count), c);
            }
        }));
        claude.Add(new(ClaudeLogKind.Action, "Removed silence 3×", "trim_segment ×3 · −15.34 s", setUndone: undone =>
        {
            Clip(1).Start = undone ? 8.1 : 12.04;
            Clip(3).End = undone ? 410.6 : 404;
            Clip(5).Start = undone ? 745.2 : 750;
        }));
        claude.Add(new(ClaudeLogKind.Action, "Labeled clips from transcript", "set_label ×5", setUndone: undone =>
        {
            for (int id = 1; id <= 5; id++)
                Clip(id).Label = undone ? "Segment " + id : names[id - 1];
        }));
        claude.Add(new(ClaudeLogKind.Claude, "Output is 7:32.000, under your 8 minute target. Clip 4 has a 6 s pause around 00:09:00. Want me to cut it?"));
    }

    private static void AddAiLog(EditorViewModel editor, string text)
    {
        var claude = editor.Claude;
        ClipViewModel Clip(int id) => editor.Clips.First(c => c.Id == id);
        Clip(2).Start = 123.14;
        Clip(5).End = 812.4;

        claude.Add(new(ClaudeLogKind.User, text));
        claude.Add(new(ClaudeLogKind.Action, "Trimmed clip 2 in-point", "trim_segment · +4.54 s", [2], -4.54, changed: true,
            setUndone: undone => Clip(2).Start = undone ? 118.6 : 123.14));
        claude.Add(new(ClaudeLogKind.Action, "Trimmed clip 5 out-point", "trim_segment · −16.32 s", [5], -16.32, changed: true,
            setUndone: undone => Clip(5).End = undone ? 828.72 : 812.4));
        claude.Add(new(ClaudeLogKind.Live, "Removing pause in clip 4", "detect_silence · 08:15 – 10:02", [4]));
    }
}
