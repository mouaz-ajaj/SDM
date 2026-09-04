using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SDM.Desktop.Services;
using SDM.Desktop.ViewModels;

namespace SDM.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private readonly DialogSaveLocationPicker? _dialogs;
    private readonly SystemShell? _shell;

    private bool _shutdownStarted;

    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainWindowViewModel viewModel, DialogSaveLocationPicker dialogs, SystemShell shell)
        : this()
    {
        DataContext = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
    }

    /// <summary>Restores the previous session once the window is up, not during construction.</summary>
    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        _dialogs?.Attach(this);

        // The clipboard belongs to a window, and this is the first moment there is one.
        _shell?.Attach(this);

        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ShowRequested += OnShowRequested;
            await viewModel.LoadAsync();
        }
    }

    /// <summary>
    /// Brings the window forward for a second launch of SDM, which sends this and then
    /// exits rather than starting a copy that would fight the first over the pipe, the
    /// database and the partial files.
    /// </summary>
    private void OnShowRequested(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
    }

    /// <summary>
    /// Selects the row being right-clicked. Without this the menu would act on the row
    /// under the pointer while the detail panel below still described a different one.
    /// </summary>
    private void OnRowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            return;
        }

        if (sender is Control { DataContext: DownloadItemViewModel row }
            && DataContext is MainWindowViewModel viewModel)
        {
            viewModel.Selected = row;
        }
    }

    /// <summary>
    /// Holds the window open just long enough for in-flight transfers to be stopped and
    /// their state written. Exiting immediately would lose the last status of each row.
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Every close runs the shutdown, not only one with transfers in flight. Closing an
        // idle window used to skip it entirely, which left the browser bridge listening and
        // its last rows unflushed, and made the container the first thing to try stopping
        // the bridge — after the dispatcher had already gone.
        if (!_shutdownStarted && DataContext is MainWindowViewModel viewModel)
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
