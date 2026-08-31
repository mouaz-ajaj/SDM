namespace SDM.Application.Settings;

/// <summary>
/// The settings a user can change from inside the application. Deliberately a subset:
/// everything here is safe to expose, and the rest stays in the shipped configuration
/// file where it belongs.
/// </summary>
public sealed record UserSettings
{
    /// <summary>Empty means the system Downloads folder.</summary>
    public string DownloadFolder { get; init; } = string.Empty;

    public bool OrganizeIntoCategoryFolders { get; init; } = true;

    public bool AskWhereToSave { get; init; }

    public int MaximumConcurrent { get; init; } = 3;

    public int MaximumPerHost { get; init; } = 2;

    public int MaximumConnectionsPerHost { get; init; } = 6;

    public int MaximumSegments { get; init; } = 4;

    public int MaximumAttempts { get; init; } = 4;

    public int IdleTimeoutSeconds { get; init; } = 60;
}
