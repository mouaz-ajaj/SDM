using Microsoft.Extensions.DependencyInjection;
using SDM.Desktop.Services;
using SDM.Desktop.ViewModels;
using SDM.Desktop.Views;

namespace SDM.Desktop;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IUiThread, AvaloniaUiThread>();

        services.AddSingleton<StorageProviderFolderPicker>();
        services.AddSingleton<IFolderPicker>(
            provider => provider.GetRequiredService<StorageProviderFolderPicker>());

        services.AddSingleton<SystemShell>();
        services.AddSingleton<ISystemShell>(
            provider => provider.GetRequiredService<SystemShell>());

        services.AddSingleton<DialogSaveLocationPicker>();
        services.AddSingleton<ISaveLocationPicker>(
            provider => provider.GetRequiredService<DialogSaveLocationPicker>());
        services.AddSingleton<IAppDialogs>(
            provider => provider.GetRequiredService<DialogSaveLocationPicker>());

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SettingsWindow>();
        return services;
    }
}
