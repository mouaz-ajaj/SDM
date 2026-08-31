using Microsoft.Extensions.DependencyInjection;
using SDM.Desktop.ViewModels;
using SDM.Desktop.Views;

namespace SDM.Desktop;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmDesktop(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTransient<MainWindowViewModel>();
        services.AddTransient<MainWindow>();
        return services;
    }
}
