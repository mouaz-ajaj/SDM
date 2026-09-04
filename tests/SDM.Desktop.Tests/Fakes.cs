using Microsoft.Extensions.Options;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;
using SDM.Application.Integration;
using SDM.Core.Downloads;
using SDM.Desktop.Services;

namespace SDM.Desktop.Tests;

/// <summary>
/// A scheduler under the test's control: it can be told to succeed, to throw whatever it
/// likes, or to sit still until it is cancelled.
/// </summary>
internal sealed class FakeScheduler : IDownloadScheduler
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Func<DownloadCallbacks?, CancellationToken, Task<DownloadResult>> OnEnqueue { get; set; } =
        (_, _) => Task.FromResult(new DownloadResult(@"C:\Downloads\file.bin", 1024));

    public List<string> Enqueued { get; } = [];

    /// <summary>The destination each transfer was handed, so a test can see what it got.</summary>
    public List<DownloadDestination?> Destinations { get; } = [];

    public string? DiscardedPath { get; private set; }

    /// <summary>Completes once a transfer has actually reached the scheduler.</summary>
    public Task Started => _started.Task;

    public Task<DownloadResult> EnqueueAsync(
        string address,
        DownloadCallbacks? callbacks = null,
        DownloadDestination? destination = null,
        RequestContext? context = null,
        CancellationToken cancellationToken = default)
    {
        lock (Enqueued)
        {
            Enqueued.Add(address);
            Destinations.Add(destination);
        }

        _started.TrySetResult();
        return OnEnqueue(callbacks, cancellationToken);
    }

    public Task<DownloadProbe> ProbeAsync(string address, CancellationToken cancellationToken = default) =>
        Task.FromResult(new DownloadProbe("file.bin", 1024, null, FileCategory.Other, SupportsResume: true));

    public void Discard(string destinationPath) => DiscardedPath = destinationPath;

    /// <summary>A transfer that never finishes on its own, only when cancelled.</summary>
    public static async Task<DownloadResult> BlockUntilCancelledAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        throw new UnreachableException();
    }
}

internal sealed class UnreachableException() : Exception("Not reached.");

internal sealed class FakeRepository : IDownloadRepository
{
    public List<DownloadJob> Saved { get; } = [];

    public List<Guid> Deleted { get; } = [];

    public IReadOnlyList<DownloadJob> Restored { get; set; } = [];

    /// <summary>Lets a test make reading the list fail the way a corrupt database does.</summary>
    public Func<IReadOnlyList<DownloadJob>>? OnGetAll { get; set; }

    public Task<IReadOnlyList<DownloadJob>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(OnGetAll is null ? Restored : OnGetAll());

    public Task SaveAsync(DownloadJob job, CancellationToken cancellationToken = default)
    {
        lock (Saved)
        {
            Saved.Add(job);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        Deleted.Add(id);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Runs callbacks where they are raised, so a test sees what they set.
///
/// The real one hops to Avalonia dispatcher. A test process has no dispatcher pumping,
/// so posted work never ran at all — and every assertion about what the engine callbacks
/// set was passing by never being reached. This is the whole reason the marshaller is
/// injected rather than called directly.
/// </summary>
internal sealed class ImmediateUiThread : IUiThread
{
    public void Invoke(Action action) => action();
}

internal sealed class FakeShell : ISystemShell
{
    public bool Open(string path) => true;

    public bool Reveal(string path) => true;

    public Task CopyAsync(string text) => Task.CompletedTask;
}

/// <summary>Records what it was asked and answers whatever the test decided.</summary>
internal sealed class FakeDialogs : IAppDialogs
{
    public bool Answer { get; set; }

    public int TimesAsked { get; private set; }

    public string? LastMessage { get; private set; }

    public Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        TimesAsked++;
        LastMessage = message;
        return Task.FromResult(Answer);
    }

    public Task ShowSettingsAsync() => Task.CompletedTask;
}

internal sealed class FakeSaveLocationPicker : ISaveLocationPicker
{
    public Task<DownloadDestination?> PickAsync(string address, DownloadProbe probe, string startingDirectory) =>
        Task.FromResult<DownloadDestination?>(null);
}

internal sealed class FakeBridge : IBrowserBridge
{
    public event EventHandler<BridgeMessage>? DownloadRequested;

    public event EventHandler? ShowRequested;

    public bool IsRunning => true;

    public string Address => @"\\.\pipe\test";

    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Raise(BridgeMessage message) => DownloadRequested?.Invoke(this, message);

    public void RaiseShow() => ShowRequested?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeDownloadFolder : IDownloadFolder
{
    public string GetPath() => @"C:\Downloads";
}

internal sealed class FakeApplicationInfo : IApplicationInfoService
{
    public string Name => "SDM";

    public string FullName => "Speed Download Manager";

    public string Version => "0.1.0";
}

internal sealed class StaticOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
