namespace SDM.Core.Downloads;

/// <summary>
/// A transfer failure described well enough to act on: <see cref="IsTransient"/> marks
/// the ones worth retrying, and <see cref="RetryAfter"/> carries the server's own
/// instruction about when to come back.
/// </summary>
public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException()
        : this("The download failed.")
    {
    }

    public DownloadFailedException(string message)
        : base(message)
    {
    }

    public DownloadFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public DownloadFailedException(
        string message,
        int? statusCode,
        TimeSpan? retryAfter,
        bool isTransient,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
        IsTransient = isTransient;
    }

    public int? StatusCode { get; }

    /// <summary>The server's requested wait, from a <c>Retry-After</c> header.</summary>
    public TimeSpan? RetryAfter { get; }

    public bool IsTransient { get; }
}
