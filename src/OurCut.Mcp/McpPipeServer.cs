using System.IO.Pipes;
using System.Reflection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace OurCut.Mcp;

/// <summary>Where the editor listens for MCP connections: a named pipe only the current user can open.</summary>
public static class McpEndpoint
{
    public static string PipeName { get; } =
        "ourcut-mcp-" + new string([.. Environment.UserName.Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-')]);

    public static string Version { get; } =
        typeof(McpEndpoint).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    public static Implementation ServerInfo => new() { Name = EditorTools.ServerName, Title = "OurCut", Version = Version };
}

/// <summary>
/// The editor's MCP server. Every connection on the pipe (normally one: the <c>OurCut.exe mcp</c> bridge
/// that Claude starts) gets its own MCP session with the editor tools. Only one editor serves a pipe name,
/// the one holding its lock file: a second OurCut window waits until the first one closes.
/// </summary>
public sealed class McpPipeServer(IEditorHost host, string? pipeName = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly IReadOnlyList<McpServerTool> _tools = EditorTools.Create(host);
    private Task? _listening;
    private int _sessions;
    private volatile bool _isListening;
    private volatile bool _inUse;
    private FileStream? _lock;

    public string PipeName { get; } = pipeName ?? McpEndpoint.PipeName;

    /// <summary>
    /// Held open (exclusively) by the editor that serves <see cref="PipeName"/>; the system lets go of it
    /// when that editor exits, even if it crashes. On Unix the pipe is a socket file that a crashed editor
    /// leaves behind, so the pipe itself cannot tell whether its owner is still running.
    /// </summary>
    public string LockPath => Path.Combine(Path.GetTempPath(), PipeName + ".lock");

    /// <summary>How often a second editor checks whether the first one has closed.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Open MCP sessions.</summary>
    public int Sessions => Volatile.Read(ref _sessions);

    /// <summary>Claude can connect.</summary>
    public bool IsListening => _isListening;

    /// <summary>Another editor (another OurCut window) serves the pipe; this one waits for it to close.</summary>
    public bool IsInUseElsewhere => _inUse;

    /// <summary>Raised on a background thread when a session starts or ends, or listening starts or stops.</summary>
    public event EventHandler? StateChanged;

    public void Start() => _listening ??= Task.Run(() => ListenAsync(_stop.Token));

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!TryClaimName() || TryCreatePipe(first: !_isListening) is not { } pipe)
                {
                    // Another editor has the pipe (or it could not be created): try again in a while.
                    await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
                    continue;
                }
                SetState(listening: true, inUse: false);
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }
                catch
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
                _ = Task.Run(() => ServeAsync(pipe, ct), CancellationToken.None);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            SetState(listening: false, inUse: false);
            if (_lock is not null)
                await _lock.DisposeAsync().ConfigureAwait(false);
            _lock = null;
        }
    }

    /// <summary>Takes the lock file, unless another editor has it.</summary>
    private bool TryClaimName()
    {
        if (_lock is not null)
            return true;
        try
        {
            _lock = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetState(listening: false, inUse: true);
            return false;
        }
    }

    /// <summary>
    /// Later instances wait for the next connection while earlier ones serve. On Windows the first one also
    /// claims the name (<see cref="PipeOptions.FirstPipeInstance"/>) against other programs; on Unix that
    /// option fails on a socket file left by a crashed editor, and the lock file already decides.
    /// </summary>
    private NamedPipeServerStream? TryCreatePipe(bool first)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly
            | (first && OperatingSystem.IsWindows() ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        try
        {
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte, options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            SetState(listening: false, inUse: first);
            return null;
        }
    }

    private void SetState(bool listening, bool inUse)
    {
        if (_isListening == listening && _inUse == inUse)
            return;
        _isListening = listening;
        _inUse = inUse;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        Interlocked.Increment(ref _sessions);
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            var options = new McpServerOptions
            {
                ServerInfo = McpEndpoint.ServerInfo,
                ServerInstructions = EditorTools.Instructions,
                ToolCollection = [.. _tools],
            };
            await using var server = McpServer.Create(new StreamServerTransport(pipe, pipe, EditorTools.ServerName), options);
            await server.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The bridge went away or the editor is closing.
        }
        finally
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            Interlocked.Decrement(ref _sessions);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_listening is { } listening)
            await listening.ConfigureAwait(false);
        _stop.Dispose();
    }
}
