using Avalonia.Input;
using OurCut.App.ViewModels;

namespace OurCut.App.Demo;

public static partial class DemoScenario
{
    // Settings → Keyboard screens: viewState() in design/project/OurCut.dc.html (kbSel 'excl', kbRec / kbConflict).
    static partial void ApplyKeyboard(EditorViewModel editor, DesignScreen screen)
    {
        var keyboard = editor.Settings.Keyboard;
        editor.Settings.LoadKeyboardDemo();
        if (screen is not (DesignScreen.SettingsKeyboardRecording or DesignScreen.SettingsKeyboardConflict))
            return;
        keyboard.StartRecording(keyboard.Row(ShortcutAction.ToggleExclude));
        if (screen == DesignScreen.SettingsKeyboardConflict)
            keyboard.Record(Key.E, KeyModifiers.Control);
    }
}
