using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Application.Settings;
using SDM.Desktop.Services;

namespace SDM.Desktop.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly IUserSettingsStore _store;
    private readonly IFolderPicker _folderPicker;
    private readonly IDownloadFolder _defaultFolder;
    private readonly IOptionsMonitor<DownloadOptions> _options;
    private readonly ILogger<SettingsViewModel> _logger;

    [ObservableProperty]
    private string _downloadFolder = string.Empty;

    [ObservableProperty]
    private bool _organizeIntoCategoryFolders = true;

    [ObservableProperty]
    private bool _askWhereToSave;

    [ObservableProperty]
    private int _maximumConcurrent = 3;

    [ObservableProperty]
    private int _maximumPerHost = 2;

    [ObservableProperty]
    private int _maximumConnectionsPerHost = 6;

    [ObservableProperty]
    private int _maximumSegments = 4;

    [ObservableProperty]
    private int _maximumAttempts = 4;

    [ObservableProperty]
    private int _idleTimeoutSeconds = 60;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private bool _isSaved;

    public SettingsViewModel(
        IUserSettingsStore store,
        IFolderPicker folderPicker,
        IDownloadFolder downloadFolder,
        IOptionsMonitor<DownloadOptions> options,
        ILogger<SettingsViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(downloadFolder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _store = store;
        _folderPicker = folderPicker;
        _defaultFolder = downloadFolder;
        _options = options;
        _logger = logger;

        Load();
    }

    /// <summary>Shown so the user can find, back up or hand-edit the file themselves.</summary>
    public string SettingsPath => _store.Path;

    /// <summary>
    /// The connection limits are turned into semaphores when the application starts, and
    /// a semaphore cannot be resized while transfers are holding it. Everything else
    /// applies to the next download.
    /// </summary>
    public static string RestartNote => "Connection limits apply the next time SDM starts.";

    private void Load()
    {
        DownloadOptions current = _options.CurrentValue;

        DownloadFolder = string.IsNullOrWhiteSpace(current.Folder)
            ? _defaultFolder.GetPath()
            : current.Folder;

        OrganizeIntoCategoryFolders = current.OrganizeIntoCategoryFolders;
        AskWhereToSave = current.AskWhereToSave;
        MaximumConcurrent = current.MaximumConcurrent;
        MaximumPerHost = current.MaximumPerHost;
        MaximumConnectionsPerHost = current.MaximumConnectionsPerHost;
        MaximumSegments = current.MaximumSegments;
        MaximumAttempts = current.MaximumAttempts;
        IdleTimeoutSeconds = current.IdleTimeoutSeconds;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? chosen = await _folderPicker.PickAsync(DownloadFolder);

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            DownloadFolder = chosen;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        StatusMessage = null;
        IsSaved = false;

        if (!Validate(out string? problem))
        {
            StatusMessage = problem;
            return;
        }

        try
        {
            await _store.SaveAsync(new UserSettings
            {
                DownloadFolder = DownloadFolder.Trim(),
                OrganizeIntoCategoryFolders = OrganizeIntoCategoryFolders,
                AskWhereToSave = AskWhereToSave,
                MaximumConcurrent = MaximumConcurrent,
                MaximumPerHost = MaximumPerHost,
                MaximumConnectionsPerHost = MaximumConnectionsPerHost,
                MaximumSegments = MaximumSegments,
                MaximumAttempts = MaximumAttempts,
                IdleTimeoutSeconds = IdleTimeoutSeconds,
            });

            IsSaved = true;
            StatusMessage = "Saved.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(exception, "Could not save settings to {SettingsPath}.", _store.Path);
            StatusMessage = "Could not write the settings file. See the log for details.";
        }
    }

    [RelayCommand]
    private void RestoreDefaults()
    {
        UserSettings defaults = new();

        DownloadFolder = _defaultFolder.GetPath();
        OrganizeIntoCategoryFolders = defaults.OrganizeIntoCategoryFolders;
        AskWhereToSave = defaults.AskWhereToSave;
        MaximumConcurrent = defaults.MaximumConcurrent;
        MaximumPerHost = defaults.MaximumPerHost;
        MaximumConnectionsPerHost = defaults.MaximumConnectionsPerHost;
        MaximumSegments = defaults.MaximumSegments;
        MaximumAttempts = defaults.MaximumAttempts;
        IdleTimeoutSeconds = defaults.IdleTimeoutSeconds;

        StatusMessage = "Defaults restored — not saved yet.";
        IsSaved = false;
    }

    /// <summary>
    /// The same rules the application validates on startup. Checking here means a bad
    /// value is refused while it can still be corrected, rather than stopping the next
    /// launch with an error the user cannot connect to anything they did.
    /// </summary>
    private bool Validate(out string? problem)
    {
        if (string.IsNullOrWhiteSpace(DownloadFolder))
        {
            problem = "Choose a folder for downloads.";
            return false;
        }

        if (MaximumConcurrent is < 1 or > 16)
        {
            problem = "Transfers at once must be between 1 and 16.";
            return false;
        }

        if (MaximumPerHost < 1 || MaximumPerHost > MaximumConcurrent)
        {
            problem = "Transfers per site must be between 1 and the total allowed at once.";
            return false;
        }

        if (MaximumConnectionsPerHost is < 1 or > 32)
        {
            problem = "Connections per site must be between 1 and 32.";
            return false;
        }

        if (MaximumSegments is < 1 or > 16)
        {
            problem = "Parts per file must be between 1 and 16.";
            return false;
        }

        if (MaximumAttempts is < 1 or > 10)
        {
            problem = "Attempts must be between 1 and 10.";
            return false;
        }

        if (IdleTimeoutSeconds is < 5 or > 3600)
        {
            problem = "The silence limit must be between 5 and 3600 seconds.";
            return false;
        }

        problem = null;
        return true;
    }
}
