using Avalonia.Media;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

/// <summary>One line in the History tab.</summary>
public sealed class DownloadEventViewModel
{
    private static readonly SolidColorBrush Information = new(Color.Parse("#17B4FF"));
    private static readonly SolidColorBrush Warning = new(Color.Parse("#FF6B6B"));
    private static readonly SolidColorBrush Success = new(Color.Parse("#3DDC97"));

    public DownloadEventViewModel(DownloadEvent entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        TimeText = entry.At.ToLocalTime().ToString("HH:mm:ss", null);
        Text = entry.Text;

        Marker = entry.Kind switch
        {
            DownloadEventKind.Warning => Warning,
            DownloadEventKind.Success => Success,
            _ => Information,
        };
    }

    public string TimeText { get; }

    public string Text { get; }

    public IBrush Marker { get; }
}
