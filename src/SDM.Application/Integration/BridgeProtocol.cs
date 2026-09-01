namespace SDM.Application.Integration;

/// <summary>
/// The contract between the browser bridge and the running application. Both ends are
/// ours, so the wire format is one JSON object per line — simple to read in a log and
/// simple to write by hand when testing.
/// </summary>
public static class BridgeProtocol
{
    /// <summary>
    /// Pipe names are machine-wide, so the user name keeps two people signed in to the
    /// same computer from reaching each other's downloads.
    /// </summary>
    public static string PipeName =>
        $"sdm.bridge.{Environment.UserName.ToLowerInvariant()}";

    public const string Download = "download";
    public const string Ping = "ping";

    public const string Accepted = "accepted";
    public const string Pong = "pong";
    public const string Error = "error";
}

/// <summary>What the browser is asking the application to do.</summary>
public sealed record BridgeMessage
{
    public string Type { get; init; } = BridgeProtocol.Ping;

    public string? Url { get; init; }

    /// <summary>The name the browser would have used, when it knows one.</summary>
    public string? FileName { get; init; }

    public string? Referrer { get; init; }
}

/// <summary>What the application answers. Always sent, so the browser is never left waiting.</summary>
public sealed record BridgeReply
{
    public string Type { get; init; } = BridgeProtocol.Error;

    public string? Message { get; init; }

    public string? Version { get; init; }

    public static BridgeReply Accepted(string url) =>
        new() { Type = BridgeProtocol.Accepted, Message = url };

    public static BridgeReply Failed(string message) =>
        new() { Type = BridgeProtocol.Error, Message = message };
}
