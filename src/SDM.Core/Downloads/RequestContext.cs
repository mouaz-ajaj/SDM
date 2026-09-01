namespace SDM.Core.Downloads;

/// <summary>
/// What the browser knew about a download and SDM, fetching the same URL from a separate
/// process, does not: the session it was made in.
///
/// Without this, taking a download away from the browser makes it worse. A file behind a
/// login is not a file at that URL — it is a file at that URL <em>for whoever is signed
/// in</em>, and a bare request gets the sign-in page instead, saved under the name of the
/// file that was wanted. Some servers also refuse a request whose Referer is missing, and
/// a few serve different bytes to a client that does not look like the browser.
///
/// It is deliberately not persisted. A cookie is a credential, and writing one to the
/// transfer database would turn a list of downloads into a store of live sessions on disk,
/// readable by anything running as the user, outliving both the download and the session
/// itself. A transfer resumed after a restart therefore goes without it, and may fail —
/// which is the better failure of the two.
/// </summary>
public sealed record RequestContext
{
    /// <summary>The Cookie header the browser would have sent, already assembled.</summary>
    public string? Cookie { get; init; }

    /// <summary>The page the download was started from.</summary>
    public string? Referrer { get; init; }

    /// <summary>The browser's own User-Agent, so the server sees the client it expects.</summary>
    public string? UserAgent { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Cookie)
        && string.IsNullOrWhiteSpace(Referrer)
        && string.IsNullOrWhiteSpace(UserAgent);
}
