using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

/// <summary>One connection's row in the Connections tab.</summary>
public sealed partial class SegmentViewModel : ObservableObject
{
    private static readonly string[] ByteUnits = ["B", "KB", "MB", "GB", "TB"];

    [ObservableProperty]
    private double _percentage;

    [ObservableProperty]
    private string _rangeText = string.Empty;

    [ObservableProperty]
    private string _completedText = string.Empty;

    [ObservableProperty]
    private string _stateText = string.Empty;

    public SegmentViewModel(SegmentProgress progress)
    {
        Index = progress.Index;
        Update(progress);
    }

    public int Index { get; }

    public void Update(SegmentProgress progress)
    {
        ArgumentNullException.ThrowIfNull(progress);

        Percentage = progress.Percentage;
        RangeText = $"{FormatBytes(progress.Start)} – {FormatBytes(progress.End + 1)}";
        CompletedText = FormatBytes(progress.Completed);
        StateText = progress.IsComplete ? "Done" : "Running";
    }

    /// <summary>
    /// Called when the transfer as a whole finished. Segment reports are throttled, so
    /// the last few bytes of a part often arrive with no report left to carry them: the
    /// file was complete while one connection still read "Running".
    /// </summary>
    public void MarkComplete()
    {
        Percentage = 100;
        StateText = "Done";
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
