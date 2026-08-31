using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Desktop.Views;

namespace SDM.Desktop;

public sealed partial class App : Avalonia.Application
{
    private readonly IServiceProvider _services;

    public App(IServiceProvider services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));
    }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ILogger<App> logger = _services.GetRequiredService<ILogger<App>>();
            MainWindow mainWindow = _services.GetRequiredService<MainWindow>();

            logger.LogInformation("Opening the main window.");
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
