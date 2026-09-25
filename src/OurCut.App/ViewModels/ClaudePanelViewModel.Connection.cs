using CommunityToolkit.Mvvm.ComponentModel;

namespace OurCut.App.ViewModels;

/// <summary>The MCP connection as one state, for the title bar and Settings → MCP server.</summary>
public sealed partial class ClaudePanelViewModel
{
    /// <summary>"Let Claude connect" (Settings → MCP server): the editor's MCP server should run.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    public partial bool IsServerOn { get; set; } = true;

    /// <summary>When the current connection started; null while there is none.</summary>
    [ObservableProperty]
    public partial DateTimeOffset? ConnectedSince { get; set; }

    // STUB: set from Claude's clientInfo once the bridge forwards it; null reads as "Claude".
    /// <summary>The connected MCP client ("Claude Desktop").</summary>
    [ObservableProperty]
    public partial string? ClientName { get; set; }

    // STUB: the project open in the window that holds the server; the pipe lock file would have to carry it.
    /// <summary>The project open in the OurCut window that has the server.</summary>
    [ObservableProperty]
    public partial string? OtherWindowProject { get; set; }

    /// <summary>
    /// Follows <see cref="IsServerOn"/>, <see cref="IsConnected"/>, <see cref="IsWorking"/>, <see cref="IsListening"/> and
    /// <see cref="IsServedElsewhere"/>.
    /// </summary>
    public McpStatus Status => !IsServerOn ? McpStatus.Off
        : IsConnected ? (IsWorking ? McpStatus.Editing : McpStatus.Connected)
        : IsListening ? McpStatus.Waiting
        : IsServedElsewhere ? McpStatus.OtherWindow
        : McpStatus.Off;
}
