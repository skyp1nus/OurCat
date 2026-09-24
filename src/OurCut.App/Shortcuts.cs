using Avalonia.Input;
using OurCut.App.ViewModels;

namespace OurCut.App;

/// <summary>Keyboard shortcuts of the editor window (same keys as the design's status bar).</summary>
public static class Shortcuts
{
    public static bool Handle(EditorViewModel editor, Key key, KeyModifiers mods)
    {
        bool ctrl = mods.HasFlag(KeyModifiers.Control);
        var export = editor.Export;
        if (export.IsDialogOpen)
        {
            if (key == Key.Escape && export.IsConfiguring)
            {
                export.Close();
                return true;
            }
            if (key == Key.Enter && export.IsConfiguring)
            {
                export.Start();
                return true;
            }
            return false;
        }

        if (ctrl && key == Key.O)
        {
            editor.OpenFileCommand.Execute(null);
            return true;
        }
        if (!editor.HasFile)
            return false;
        if (ctrl && key == Key.E)
        {
            export.Open();
            return true;
        }
        if (ctrl)
            return false;

        switch (key)
        {
            case Key.Space:
                editor.TogglePlay();
                return true;
            case Key.Left or Key.Right:
                int dir = key == Key.Left ? -1 : 1;
                if (mods.HasFlag(KeyModifiers.Shift))
                    editor.StepSeconds(dir);
                else
                    editor.SetTime(editor.Time + dir / editor.FrameRate);
                return true;
            case Key.I:
                editor.MarkIn();
                return true;
            case Key.O:
                editor.MarkOut();
                return true;
            case Key.E or Key.Delete or Key.Back:
                editor.ToggleExclude();
                return true;
            case Key.S:
                editor.Split();
                return true;
            default:
                return false;
        }
    }
}
