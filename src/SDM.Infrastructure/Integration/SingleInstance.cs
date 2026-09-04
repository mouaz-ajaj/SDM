using System.IO.Pipes;
using System.Text.Json;
using SDM.Application.Integration;

namespace SDM.Infrastructure.Integration;

/// <summary>
/// Keeps one copy of SDM running per signed-in user, and hands a second launch over to
/// the copy that is already there.
///
/// Two copies do not merely duplicate the window. They bind the same pipe name — Windows
/// allows several servers on one name — so the browser's downloads are split between them
/// arbitrarily. They open the same SQLite file, so their writes race. They contend for the
/// same log file, and the one that loses runs with no diagnostics at all. And they can
/// resume the same partial file at the same time, which corrupts it. None of that is
/// visible to the person who simply started SDM twice.
///
/// A mutex rather than a search for a running process: it is decided by the operating
/// system, it cannot be raced, and it is released even if the first copy is killed.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan HandoverTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex? _mutex;

    private SingleInstance(bool isOnly, Mutex? mutex)
    {
        IsOnly = isOnly;
        _mutex = mutex;
    }

    /// <summary>True when this process is the one that should run.</summary>
    public bool IsOnly { get; }

    /// <summary>
    /// Claims the right to be the running copy.
    ///
    /// <paramref name="name"/> is per user for the same reason the pipe name is: two
    /// people signed in to one machine each get their own SDM, and neither can stop the
    /// other from starting one.
    /// </summary>
    public static SingleInstance Claim(string? name = null)
    {
        // Local, not Global: this is about one signed-in session, not the machine.
        string mutexName = @"Local\" + (name ?? $"sdm.instance.{Environment.UserName.ToLowerInvariant()}");

        Mutex mutex = new(initiallyOwned: false, mutexName);

        try
        {
            // Zero wait. Either it is free now or another copy holds it; there is nothing
            // worth waiting for, because the other copy is not about to exit.
            return new SingleInstance(mutex.WaitOne(TimeSpan.Zero, exitContext: false), mutex);
        }
        catch (AbandonedMutexException)
        {
            // The previous copy was killed without releasing it. Holding an abandoned
            // mutex is holding it: this process is now the only one.
            return new SingleInstance(isOnly: true, mutex);
        }
    }

    /// <summary>
    /// Asks the copy that is already running to show itself, so a second launch brings
    /// the window forward instead of appearing to do nothing at all.
    ///
    /// Best effort by design. If the running copy cannot be reached this process still
    /// has to exit — starting a second one is the thing being prevented — so a failure
    /// here changes nothing except that the window stays where it was.
    /// </summary>
    public static async Task<bool> AskRunningInstanceToShowAsync(
        string? pipeName = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await using NamedPipeClientStream pipe = new(
                ".", pipeName ?? BridgeProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);

            await pipe.ConnectAsync((int)HandoverTimeout.TotalMilliseconds, cancellationToken)
                .ConfigureAwait(false);

            await using StreamWriter writer = new(pipe, leaveOpen: true) { AutoFlush = true };

            await writer.WriteLineAsync(
                JsonSerializer.Serialize(new BridgeMessage { Type = BridgeProtocol.Show }, Json).AsMemory(),
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception exception) when (
            exception is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        if (IsOnly)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Never held, or already released. Disposing below is all that is left.
            }
        }

        _mutex.Dispose();
    }
}
