using CommunityToolkit.Mvvm.ComponentModel;

namespace SDM.Desktop.ViewModels;

/// <summary>One row in the sidebar: a filter, how many match it, and whether it is chosen.</summary>
public sealed partial class FilterOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    public FilterOptionViewModel(TransferFilter filter)
    {
        Filter = filter;
        Name = TransferFilters.NameOf(filter);
    }

    public TransferFilter Filter { get; }

    public string Name { get; }
}
