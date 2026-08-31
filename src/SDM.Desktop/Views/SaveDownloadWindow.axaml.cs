using Avalonia.Controls;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Views;

public sealed partial class SaveDownloadWindow : Window
{
    public SaveDownloadWindow()
    {
        InitializeComponent();
    }

    public SaveDownloadWindow(SaveDownloadViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        viewModel.Closed += (_, _) => Close();
    }
}
