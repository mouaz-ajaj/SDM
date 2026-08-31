using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SDM.Application.Settings;

namespace SDM.Infrastructure.Settings;

/// <summary>
/// Writes the user's settings into the same JSON shape the configuration layer reads, so
/// a saved preference is picked up exactly like one typed by hand. Keys the settings
/// screen does not manage are left untouched: the file is the user's, not this class's.
/// </summary>
public sealed class JsonUserSettingsStore : IUserSettingsStore
{
    private const string DownloadsSection = "Downloads";

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly ILogger<JsonUserSettingsStore> _logger;

    /// <param name="path">
    /// Where to write. Null uses the per-user location; tests pass a temporary path so
    /// they never fight each other over the one real file.
    /// </param>
    public JsonUserSettingsStore(ILogger<JsonUserSettingsStore> logger, string? path = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        Path = path ?? SdmPaths.UserSettingsPath;
    }

    public string Path { get; }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        System.IO.Directory.CreateDirectory(
            System.IO.Path.GetDirectoryName(Path) ?? SdmPaths.EnsureUserDataDirectory());

        JsonObject root = await ReadExistingAsync(cancellationToken);
        JsonObject downloads = root[DownloadsSection] as JsonObject ?? [];

        downloads["Folder"] = settings.DownloadFolder;
        downloads["OrganizeIntoCategoryFolders"] = settings.OrganizeIntoCategoryFolders;
        downloads["AskWhereToSave"] = settings.AskWhereToSave;
        downloads["MaximumConcurrent"] = settings.MaximumConcurrent;
        downloads["MaximumPerHost"] = settings.MaximumPerHost;
        downloads["MaximumConnectionsPerHost"] = settings.MaximumConnectionsPerHost;
        downloads["MaximumSegments"] = settings.MaximumSegments;
        downloads["MaximumAttempts"] = settings.MaximumAttempts;
        downloads["IdleTimeoutSeconds"] = settings.IdleTimeoutSeconds;

        root[DownloadsSection] = downloads;

        // Written whole and replaced in one move, so a crash mid-write cannot leave the
        // user with a half-written file the application then refuses to start with.
        string temporary = Path + ".tmp";
        await File.WriteAllTextAsync(temporary, root.ToJsonString(WriteOptions), cancellationToken);
        File.Move(temporary, Path, overwrite: true);

        _logger.LogInformation("Saved user settings to {SettingsPath}.", Path);
    }

    private async Task<JsonObject> ReadExistingAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(Path))
        {
            return [];
        }

        try
        {
            string json = await File.ReadAllTextAsync(Path, cancellationToken);
            return JsonNode.Parse(json) as JsonObject ?? [];
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            // An unreadable file is replaced rather than allowed to block saving, but the
            // reason is recorded so it is not a silent loss.
            _logger.LogWarning(exception, "Could not read {SettingsPath}; it will be rewritten.", Path);
            return [];
        }
    }
}
