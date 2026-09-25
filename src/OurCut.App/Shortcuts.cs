using Avalonia.Input;
using OurCut.App.ViewModels;

namespace OurCut.App;

/// <summary>Keyboard shortcuts of the editor window: the dialogs' own keys, then the key map of Settings → Keyboard.</summary>
public static class Shortcuts
{
    public static bool Handle(EditorViewModel editor, Key key, KeyModifiers mods)
    {
        var keyboard = editor.Settings.Keyboard;
        if (keyboard.IsRecording)
        {
            keyboard.Record(key, mods);
            return true;
        }

        var export = editor.Export;
        if (export.IsDialogOpen)
        {
            if (key == Key.Escape)
            {
                export.Dismiss();
                return true;
            }
            if (key == Key.Enter && export.IsConfiguring)
            {
                export.StartCommand.Execute(null);
                return true;
            }
            return false;
        }

        if (editor.Settings.IsOpen)
        {
            if (key == Key.Escape)
            {
                // Esc first drops a shortcut conflict, then closes the dialog.
                if (!keyboard.CancelConflict())
                    editor.Settings.Close();
                return true;
            }
            return false;
        }

        // The editor's own shortcuts: whatever Settings → Keyboard gives the key.
        return KeyCombo.From(key, mods) is { } combo && editor.Settings.KeyMap.Find(combo) is { } action && Run(editor, action);
    }

    /// <summary>Runs a shortcut's action; false when it does not apply now (all but opening need a video).</summary>
    public static bool Run(EditorViewModel editor, ShortcutAction action)
    {
        switch (action)
        {
            case ShortcutAction.OpenVideo:
                editor.OpenFileCommand.Execute(null);
                return true;
            case ShortcutAction.OpenProject:
                editor.OpenProjectCommand.Execute(null);
                return true;
        }
        if (!editor.HasFile)
            return false;
        switch (action)
        {
            case ShortcutAction.PlayPause:
                editor.TogglePlay();
                break;
            case ShortcutAction.PreviousFrame or ShortcutAction.NextFrame:
                editor.SetTime(editor.Time + (action == ShortcutAction.PreviousFrame ? -1 : 1) / editor.FrameRate);
                break;
            case ShortcutAction.JumpBack:
                editor.Jump(-1);
                break;
            case ShortcutAction.JumpForward:
                editor.Jump(1);
                break;
            case ShortcutAction.SetIn:
                editor.MarkIn();
                break;
            case ShortcutAction.SetOut:
                editor.MarkOut();
                break;
            case ShortcutAction.Split:
                editor.Split();
                break;
            case ShortcutAction.ToggleExclude:
                editor.ToggleExclude();
                break;
            case ShortcutAction.DeleteClip:
                editor.DeleteClip();
                break;
            case ShortcutAction.SelectTool:
                editor.Tool = TimelineTool.Select;
                break;
            case ShortcutAction.Undo:
                editor.Undo();
                break;
            case ShortcutAction.Redo:
                editor.Redo();
                break;
            case ShortcutAction.Save:
                editor.SaveProjectCommand.Execute(null);
                break;
            case ShortcutAction.SaveAs:
                editor.SaveProjectAsCommand.Execute(null);
                break;
            case ShortcutAction.Export:
                editor.Export.Open();
                break;
            case ShortcutAction.ZoomIn:
                editor.ZoomInCommand.Execute(null);
                break;
            case ShortcutAction.ZoomOut:
                editor.ZoomOutCommand.Execute(null);
                break;
            case ShortcutAction.FitTimeline:
                editor.ZoomFitCommand.Execute(null);
                break;
            default:
                return false;
        }
        return true;
    }
}
