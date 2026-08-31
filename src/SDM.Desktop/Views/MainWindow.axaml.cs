using Avalonia.Controls;
using Avalonia.Interactivity;
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

    /// <summary>Restores the previous session once the window is up, not during construction.</summary>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            await viewModel.LoadAsync();
        }
    }

    /// <summary>
    /// Holds the window open just long enough for in-flight transfers to be stopped and
    /// their state written. Exiting immediately would lose the last status of each row.
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
