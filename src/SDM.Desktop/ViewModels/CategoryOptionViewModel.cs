using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SDM.Core.Downloads;

namespace SDM.Desktop.ViewModels;

/// <summary>A category row in the sidebar, with the colour its file icons use.</summary>
public sealed partial class CategoryOptionViewModel : ObservableObject
{
    [ObservableProperty]
    private int _count;

    public CategoryOptionViewModel(FileCategory category)
    {
        Category = category;
        Name = FileCategories.FolderNameFor(category);
        Marker = new SolidColorBrush(Color.Parse(CategoryColours.HexFor(category)));
    }

    public FileCategory Category { get; }

    public string Name { get; }

    public IBrush Marker { get; }
}

/// <summary>
/// One colour per category, used by both the sidebar dots and the row icons so a glance
/// at either means the same thing.
/// </summary>
public static class CategoryColours
{
    public static string HexFor(FileCategory category) => category switch
    {
        FileCategory.Documents => "#C084FC",
        FileCategory.Compressed => "#3DDC97",
        FileCategory.Programs => "#17B4FF",
        FileCategory.Video => "#FF9F6B",
        FileCategory.Audio => "#F472B6",
        FileCategory.Images => "#FBBF24",
        _ => "#64798F",
    };
}
