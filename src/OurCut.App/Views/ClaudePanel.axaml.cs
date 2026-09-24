using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using OurCut.App.ViewModels;

namespace OurCut.App.Views;

public partial class ClaudePanel : UserControl
{
    private ClaudePanelViewModel? _vm;

    public ClaudePanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as ClaudePanelViewModel);
    }

    private void Attach(ClaudePanelViewModel? vm)
    {
        if (_vm is not null)
            _vm.Log.CollectionChanged -= OnLogChanged;
        _vm = vm;
        if (_vm is not null)
            _vm.Log.CollectionChanged += OnLogChanged;
        ScrollToEnd();
    }

    private void OnLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => ScrollToEnd();

    /// <summary>Keeps the newest log entry in view.</summary>
    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() =>
        {
            if (_vm is { Log.Count: > 0 })
                LogScroller.ScrollToEnd();
            else
                LogScroller.ScrollToHome();
        }, DispatcherPriority.Background);

    private void OnHeaderPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_vm is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _vm.IsOpen = !_vm.IsOpen;
            ScrollToEnd();
        }
    }

    private void OnComposerKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _vm is not null)
        {
            _vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
