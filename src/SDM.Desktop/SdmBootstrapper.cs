using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Application;
using SDM.Database;
using SDM.Infrastructure;
using SDM.Infrastructure.Logging;

namespace SDM.Desktop;

public static class SdmBootstrapper
{
    private const string UserSettingsFileName = SdmPaths.UserSettingsFileName;

    /// <param name="userSettingsDirectory">
    /// Where the user's own settings file lives. Null uses the per-user location.
    /// </param>
    public static ServiceProvider CreateServiceProvider(
        string? basePath = null, string? userSettingsDirectory = null)
    {
        IConfiguration configuration = BuildConfiguration(basePath, userSettingsDirectory);
        ServiceCollection services = new();

        services.AddSingleton(configuration);
        services.AddLogging(builder =>
        {
            builder.ClearProviders();

            // The console provider only reaches anyone when stdout has been redirected;
            // the file is what a user running the packaged application can actually read.
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
            builder.AddSdmFileLogging(configuration);
            builder.SetMinimumLevel(LogLevel.Information);
        });

        services.AddSdmApplication(configuration);
        services.AddSdmInfrastructure();
        services.AddSdmDatabase(configuration);
        services.AddSdmDesktop();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>The user's own settings file, which survives reinstalling and rebuilding.</summary>
    public static string UserSettingsPath => SdmPaths.UserSettingsPath;

    private static IConfiguration BuildConfiguration(string? basePath, string? userSettingsDirectory)
    {
        string contentRoot = basePath ?? AppContext.BaseDirectory;

        return new ConfigurationBuilder()
            .SetBasePath(contentRoot)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)

            // Layered on top, and deliberately outside the installation: the shipped file
            // is replaced by every build and every update, so a preference written there
            // would quietly disappear. Only the keys the user changed need be present.
            .AddJsonFile(
                new PhysicalFileProvider(userSettingsDirectory ?? EnsureUserSettingsDirectory()),
                UserSettingsFileName,
                optional: true,

                // Watched, so a setting saved from inside the application takes effect on
                // the next download rather than the next launch.
                reloadOnChange: true)
            .Build();
    }

    private static string EnsureUserSettingsDirectory() => SdmPaths.EnsureUserDataDirectory();
}
