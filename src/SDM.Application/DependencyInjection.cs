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
            .Validate(options => options.MaximumPerHost is > 0 and <= 16,
                "Downloads:MaximumPerHost must be between 1 and 16.")
            .Validate(options => options.MaximumPerHost <= options.MaximumConcurrent,
                "Downloads:MaximumPerHost cannot exceed Downloads:MaximumConcurrent.")
            .Validate(options => options.MaximumConnectionsPerHost is > 0 and <= 32,
                "Downloads:MaximumConnectionsPerHost must be between 1 and 32.")
            .Validate(options => options.MaximumSegments is > 0 and <= 16,
                "Downloads:MaximumSegments must be between 1 and 16.")
            .Validate(options => options.SegmentThresholdBytes >= 0,
                "Downloads:SegmentThresholdBytes cannot be negative.")
            .Validate(options => options.MaximumAttempts is > 0 and <= 10,
                "Downloads:MaximumAttempts must be between 1 and 10.")
            .Validate(options => options.IdleTimeoutSeconds is >= 5 and <= 3600,
                "Downloads:IdleTimeoutSeconds must be between 5 and 3600.")
            .Validate(options => options.MaximumRetryDelaySeconds is > 0 and <= 3600,
                "Downloads:MaximumRetryDelaySeconds must be between 1 and 3600.")
            .ValidateOnStart();

        services.AddSingleton<IApplicationInfoService, ApplicationInfoService>();
        services.AddSingleton<IStartDownloadUseCase, StartDownloadUseCase>();
        services.AddSingleton<IDownloadScheduler, DownloadScheduler>();
        services.AddSingleton<IConnectionBudget, HostConnectionBudget>();
        services.AddSingleton<IDownloadLayout, CategoryDownloadLayout>();
        return services;
    }
}
