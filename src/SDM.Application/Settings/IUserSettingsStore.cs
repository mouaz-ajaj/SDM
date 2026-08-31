namespace SDM.Application.Settings;

/// <summary>
/// Reads and writes the user's own settings file. Kept apart from configuration binding
/// because writing is a different concern from reading, and only some settings may be
/// written at all.
/// </summary>
public interface IUserSettingsStore
{
    /// <summary>Where the file is, so the settings screen can show it.</summary>
    string Path { get; }

    Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default);
}
