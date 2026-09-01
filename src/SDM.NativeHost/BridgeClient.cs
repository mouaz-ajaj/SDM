using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SDM.Application.Integration;

namespace SDM.NativeHost;

/// <summary>
/// Carries one request to the running application over its named pipe. When the
/// application is not running it is started and the request retried — the host itself
/// never downloads anything, so there is only ever one engine no matter how many
/// browsers are connected.
/// </summary>
public sealed class BridgeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeSpan _connectTimeout;
    private readonly TimeSpan _startupTimeout;
    private readonly Func<bool> _startApplication;
    private readonly string _pipeName;

    public BridgeClient(
        TimeSpan? connectTimeout = null,
        TimeSpan? startupTimeout = null,
        Func<bool>? startApplication = null,
        string? pipeName = null)
    {
        _connectTimeout = connectTimeout ?? TimeSpan.FromSeconds(2);
        _startupTimeout = startupTimeout ?? TimeSpan.FromSeconds(20);
        _startApplication = startApplication ?? StartApplication;
        _pipeName = pipeName ?? BridgeProtocol.PipeName;
    }

    public async Task<BridgeReply> SendAsync(BridgeMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            return await TrySendAsync(message, _pipeName, _connectTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            // Nothing is listening. Starting SDM is the point of the bridge: a link sent
            // from the browser should work whether or not the window happens to be open.
        }

        if (!_startApplication())
        {
            return BridgeReply.Failed("SDM is not running and could not be started.");
        }

        try
        {
            return await TrySendAsync(message, _pipeName, _startupTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return BridgeReply.Failed("SDM did not answer in time.");
        }
    }

    private static async Task<BridgeReply> TrySendAsync(
        BridgeMessage message, string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using NamedPipeClientStream pipe = new(
            ".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);

        await using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };
        using StreamReader reader = new(pipe, leaveOpen: true);

        await writer.WriteLineAsync(JsonSerializer.Serialize(message, Json).AsMemory(), cancellationToken);

        string? line = await reader.ReadLineAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(line)
            ? BridgeReply.Failed("SDM closed the connection without answering.")
            : JsonSerializer.Deserialize<BridgeReply>(line, Json)
              ?? BridgeReply.Failed("SDM sent an answer that could not be read.");
    }

    /// <summary>
    /// The application sits beside this executable, since the browser launches the host
    /// from wherever SDM was installed.
    /// </summary>
    private static bool StartApplication()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "SDM.Desktop.exe");

        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using Process? started = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                WorkingDirectory = AppContext.BaseDirectory,
            });

            return started is not null;
        }
        catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
