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

        services.AddSingleton<StorageProviderSaveLocationPicker>();
        services.AddSingleton<ISaveLocationPicker>(
            provider => provider.GetRequiredService<StorageProviderSaveLocationPicker>());
        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
        return services;
    }
}
