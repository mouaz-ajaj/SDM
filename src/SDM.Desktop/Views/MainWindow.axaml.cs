using Avalonia.Controls;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool _shutdownStarted;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
    }

    /// <summary>
    /// Holds the window open just long enough for in-flight transfers to be cancelled and
    /// their partial files cleaned up. Exiting immediately would orphan every .part file.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_shutdownStarted && DataContext is MainWindowViewModel viewModel && viewModel.HasActiveDownloads)
        {
            _shutdownStarted = true;
            e.Cancel = true;
            _ = ShutdownThenCloseAsync(viewModel);
            return;
        }

        base.OnClosing(e);
    }

    private async Task ShutdownThenCloseAsync(MainWindowViewModel viewModel)
    {
        await viewModel.ShutdownAsync();
        Close();
    }
}
