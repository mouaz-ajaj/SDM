using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;
using SDM.Application.Integration;

namespace SDM.Infrastructure.Integration;

/// <summary>
/// Accepts requests from the native messaging host over a named pipe. A pipe rather than
/// a socket because it needs no port, cannot be reached from the network at all, and can
/// be restricted to the current user by the operating system.
/// </summary>
public sealed class NamedPipeBrowserBridge : IBrowserBridge
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly IApplicationInfoService _applicationInfo;
    private readonly ILogger<NamedPipeBrowserBridge> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;

    private Task? _acceptLoop;

    /// <summary>
    /// The server instance currently waiting for a browser, so shutdown can break that
    /// wait. Read and written from two threads, hence volatile.
    /// </summary>
    private volatile NamedPipeServerStream? _waiting;

    /// <param name="pipeName">
    /// Null uses the per-user name. Tests pass their own: the real name is taken by any
    /// running copy of SDM, and a test that quietly talks to the live application
    /// instead of its own bridge proves nothing.
    /// </param>
    public NamedPipeBrowserBridge(
        IApplicationInfoService applicationInfo,
        ILogger<NamedPipeBrowserBridge> logger,
        string? pipeName = null)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _logger = logger;
        _pipeName = pipeName ?? BridgeProtocol.PipeName;
    }

    public event EventHandler<BridgeMessage>? DownloadRequested;

    public bool IsRunning { get; private set; }

    public string Address => @"\\.\pipe\" + _pipeName;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_acceptLoop is not null)
        {
            return Task.CompletedTask;
        }

        IsRunning = true;
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token), CancellationToken.None);

        _logger.LogInformation("Browser bridge listening on {Address}.", Address);
        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                // A fresh server instance per connection: one misbehaving client cannot
                // hold the pipe and lock every later request out.
                await using NamedPipeServerStream server = CreateServer();

                _waiting = server;
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _waiting = null;

                await ServeAsync(server, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                // Shutdown disposed the stream underneath the wait. That is the intended
                // way out of a WaitForConnectionAsync no browser is ever going to satisfy.
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One failed connection must not stop the bridge for the rest of the run.
                _logger.LogWarning(exception, "A browser bridge connection failed.");
            }
        }
    }

    private NamedPipeServerStream CreateServer() =>
        OperatingSystem.IsWindows()
            ? CreateRestrictedServer()

            // Elsewhere a named pipe is a socket in the user's own runtime directory, so
            // the file system already restricts it to one user.
            : new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

    /// <summary>
    /// Restricted to the current user. A Windows pipe is otherwise reachable by anyone
    /// signed in to the machine, and a download request is a request to write a file.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private NamedPipeServerStream CreateRestrictedServer()
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 0,
            outBufferSize: 0,
            security);
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken cancellationToken)
    {
        using StreamReader reader = new(server, leaveOpen: true);
        await using StreamWriter writer = new(server, leaveOpen: true) { AutoFlush = true };

        string? line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        BridgeReply reply = Handle(line);
        await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json).AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private BridgeReply Handle(string line)
    {
        BridgeMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(line, Json);
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "The browser bridge received something it could not read.");
            return BridgeReply.Failed("The message could not be read.");
        }

        if (message is null)
        {
            return BridgeReply.Failed("The message was empty.");
        }

        if (string.Equals(message.Type, BridgeProtocol.Ping, StringComparison.Ordinal))
        {
            return new BridgeReply
            {
                Type = BridgeProtocol.Pong,
                Version = _applicationInfo.Version,
                Message = _applicationInfo.FullName,
            };
        }

        if (!string.Equals(message.Type, BridgeProtocol.Download, StringComparison.Ordinal))
        {
            return BridgeReply.Failed($"Unknown request '{message.Type}'.");
        }

        // Everything arriving here was composed by a browser extension, so the address is
        // checked before it is allowed anywhere near the download engine.
        if (!Uri.TryCreate(message.Url, UriKind.Absolute, out Uri? source)
            || (source.Scheme != Uri.UriSchemeHttp && source.Scheme != Uri.UriSchemeHttps))
        {
            return BridgeReply.Failed("Only http and https addresses can be downloaded.");
        }

        // Nothing is listening once the application has begun closing: ShutdownAsync
        // detaches its handler before disposing the bridge, and a request arriving in that
        // window was answered "accepted" and then dropped. The extension would have told
        // the user the download had started, and no download ever existed.
        if (DownloadRequested is not { } handover)
        {
            _logger.LogWarning("Refused {Url}: SDM is closing.", source);
            return BridgeReply.Failed("SDM is closing and cannot take the download.");
        }

        _logger.LogInformation("Browser handed over {Url}.", source);
        handover(this, message with { Url = source.AbsoluteUri });

        return BridgeReply.Accepted(source.AbsoluteUri);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;

        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }

        // Cancelling the token is not enough. A WaitForConnectionAsync that no browser is
        // going to satisfy does not reliably observe cancellation on Windows, and the loop
        // task then never completes — so awaiting it below never returns, and the process
        // outlives its own window with no thread doing anything. Disposing the stream the
        // wait is sitting on is what actually ends it.
        try
        {
            _waiting?.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Racing the loop, which may have just accepted a connection and disposed it.
        }

        if (_acceptLoop is not null)
        {
            try
            {
                // Bounded, because nothing here is worth hanging an exit on. If the loop
                // ever finds a new way to get stuck, the log says so and the process still
                // closes.
                await _acceptLoop.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is being shut down.
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("The browser bridge did not stop within {Timeout}.", ShutdownTimeout);
            }
        }

        _shutdown.Dispose();
    }
}
