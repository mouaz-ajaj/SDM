using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SDM.Core.Downloads;
using SDM.Desktop.Services;

namespace SDM.Desktop.ViewModels;

/// <summary>
/// SDM's own save dialog. The system picker can only ask for a path; this one can also
/// show what the server said the file is — its real name, size and type — which is the
/// information that actually decides where someone wants to put it.
/// </summary>
public sealed partial class SaveDownloadViewModel : ObservableObject
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    private readonly IFolderPicker _folderPicker;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _fileName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _folder;

    [ObservableProperty]
    private string? _errorMessage;

    public SaveDownloadViewModel(IFolderPicker folderPicker, string address, DownloadProbe probe, string folder)
    {
        ArgumentNullException.ThrowIfNull(folderPicker);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        _folderPicker = folderPicker;
        _fileName = probe.FileName;
        _folder = folder;

        Address = address;
        SizeText = probe.TotalBytes is { } total ? FormatBytes(total) : "Unknown";
        MediaTypeText = string.IsNullOrWhiteSpace(probe.MediaType) ? "Not reported" : probe.MediaType;
        CategoryText = FileCategories.FolderNameFor(probe.Category);
        ResumeText = probe.SupportsResume ? "Yes — can be paused and resumed" : "No — cannot be resumed";
        CanResume = probe.SupportsResume;
    }

    public string Address { get; }

    public string SizeText { get; }

    public string MediaTypeText { get; }

    public string CategoryText { get; }

    public string ResumeText { get; }

    public bool CanResume { get; }

    /// <summary>Set when the dialog closes; null means the user chose not to download.</summary>
    public DownloadDestination? Result { get; private set; }

    /// <summary>Raised when the dialog should close, either way.</summary>
    public event EventHandler? Closed;

    private bool CanConfirm =>
        !string.IsNullOrWhiteSpace(FileName) && !string.IsNullOrWhiteSpace(Folder);

    [RelayCommand]
    private async Task BrowseAsync()
    {
        string? chosen = await _folderPicker.PickAsync(Folder);

        if (!string.IsNullOrWhiteSpace(chosen))
        {
            Folder = chosen;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm()
    {
        string safe = SafeFileName.Sanitize(FileName);

        // Sanitising can change what the user typed — a slash, a reserved name — so show
        // the result rather than silently writing somewhere they did not expect.
        if (!string.Equals(safe, FileName.Trim(), StringComparison.Ordinal))
        {
            FileName = safe;
            ErrorMessage = "The name was adjusted to something Windows can store. Confirm again to continue.";
            return;
        }

        Result = new DownloadDestination(Folder.Trim(), safe);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < ByteUnits.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.#} {ByteUnits[unit]}");
    }
}
