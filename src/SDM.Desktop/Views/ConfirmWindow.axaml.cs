using Avalonia.Controls;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Views;

public sealed partial class ConfirmWindow : Window
{
    public ConfirmWindow()
    {
        InitializeComponent();
    }

    public ConfirmWindow(ConfirmViewModel viewModel)
        : this()
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        DataContext = viewModel;
        viewModel.Closed += (_, _) => Close();
    }
}
