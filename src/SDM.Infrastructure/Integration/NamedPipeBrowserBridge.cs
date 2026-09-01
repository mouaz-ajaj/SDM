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

    private readonly IApplicationInfoService _applicationInfo;
    private readonly ILogger<NamedPipeBrowserBridge> _logger;
    private readonly CancellationTokenSource _shutdown = new();

    private Task? _acceptLoop;

    public NamedPipeBrowserBridge(
        IApplicationInfoService applicationInfo,
        ILogger<NamedPipeBrowserBridge> logger)
    {
        ArgumentNullException.ThrowIfNull(applicationInfo);
        ArgumentNullException.ThrowIfNull(logger);

        _applicationInfo = applicationInfo;
        _logger = logger;
    }

    public event EventHandler<BridgeMessage>? DownloadRequested;

    public bool IsRunning { get; private set; }

    public string Address => $@"\\.\pipe\{BridgeProtocol.PipeName}";

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
                await server.WaitForConnectionAsync(cancellationToken);
                await ServeAsync(server, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // One failed connection must not stop the bridge for the rest of the run.
                _logger.LogWarning(exception, "A browser bridge connection failed.");
            }
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        OperatingSystem.IsWindows()
            ? CreateRestrictedServer()

            // Elsewhere a named pipe is a socket in the user's own runtime directory, so
            // the file system already restricts it to one user.
            : new NamedPipeServerStream(
                BridgeProtocol.PipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

    /// <summary>
    /// Restricted to the current user. A Windows pipe is otherwise reachable by anyone
    /// signed in to the machine, and a download request is a request to write a file.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateRestrictedServer()
    {
        PipeSecurity security = new();
        security.AddAccessRule(new PipeAccessRule(
            WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            BridgeProtocol.PipeName,
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

        string? line = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        BridgeReply reply = Handle(line);
        await writer.WriteLineAsync(JsonSerializer.Serialize(reply, Json).AsMemory(), cancellationToken);
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

        _logger.LogInformation("Browser handed over {Url}.", source);
        DownloadRequested?.Invoke(this, message with { Url = source.AbsoluteUri });

        return BridgeReply.Accepted(source.AbsoluteUri);
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;

        if (!_shutdown.IsCancellationRequested)
        {
            await _shutdown.CancelAsync();
        }

        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected: the loop is being shut down.
            }
        }

        _shutdown.Dispose();
    }
}
