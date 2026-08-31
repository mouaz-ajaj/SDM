using Microsoft.Extensions.Options;

namespace SDM.Infrastructure.Tests;

/// <summary>
/// A fixed <see cref="IOptionsMonitor{T}"/> for tests. Production reads options through
/// a monitor so a saved setting applies to the next download rather than the next
/// launch; a test only needs one value that never changes.
/// </summary>
internal sealed class TestOptions<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue { get; } = value;

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
