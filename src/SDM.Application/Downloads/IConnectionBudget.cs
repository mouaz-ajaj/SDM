namespace SDM.Application.Downloads;

/// <summary>
/// Rations TCP connections per host. Segmenting a transfer opens several connections to
/// one server, and servers answer 429 to clients that open too many — so segments draw
/// from a shared budget rather than each transfer helping itself.
/// </summary>
public interface IConnectionBudget
{
    /// <summary>
    /// Reserves up to <paramref name="desired"/> connections for <paramref name="host"/>.
    /// Always grants at least one, waiting if necessary; the rest are granted only if
    /// they are free right now, so a lone transfer gets the whole budget and two
    /// transfers share it without either being starved.
    /// </summary>
    Task<IConnectionLease> AcquireAsync(string host, int desired, CancellationToken cancellationToken = default);
}

public interface IConnectionLease : IDisposable
{
    /// <summary>How many connections were actually granted. Never less than one.</summary>
    int Count { get; }
}
