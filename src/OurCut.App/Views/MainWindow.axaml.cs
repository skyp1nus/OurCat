using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using OurCut.App.ViewModels;

namespace OurCut.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Shortcuts are handled before focused controls (buttons, sliders) can swallow Space or the arrows.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    private EditorViewModel? Editor => DataContext as EditorViewModel;

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (Editor is not { } editor)
            return;
        if (FocusManager?.GetFocusedElement() is TextBox)
            return;
        e.Handled = Shortcuts.Handle(editor, e.Key, e.KeyModifiers);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
        Player.SetDropHover(e.DragEffects != DragDropEffects.None);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        Player.SetDropHover(false);
        var path = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p is not null);
        if (path is not null && Editor is { } editor)
            editor.OpenPath(path);
    }
}
