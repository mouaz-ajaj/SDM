using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SDM.Application.ApplicationInfo;
using SDM.Application.Downloads;

namespace SDM.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddSdmApplication(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<ApplicationInfoOptions>()
            .Bind(configuration.GetSection(ApplicationInfoOptions.SectionName))
            .Validate(options => !string.IsNullOrWhiteSpace(options.Name), "Application name is required.")
            .Validate(options => !string.IsNullOrWhiteSpace(options.FullName), "Application full name is required.")
            .ValidateOnStart();

        services.AddOptions<DownloadOptions>()
            .Bind(configuration.GetSection(DownloadOptions.SectionName))
            .Validate(options => options.MaximumConcurrent is > 0 and <= 16,
                "Downloads:MaximumConcurrent must be between 1 and 16.")
            .ValidateOnStart();

        services.AddSingleton<IApplicationInfoService, ApplicationInfoService>();
        services.AddSingleton<IStartDownloadUseCase, StartDownloadUseCase>();
        services.AddSingleton<IDownloadScheduler, DownloadScheduler>();
        return services;
    }
}
