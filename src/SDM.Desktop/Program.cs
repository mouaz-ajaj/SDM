using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;
using SDM.Infrastructure.Logging;

namespace SDM.Desktop;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        ServiceProvider? services = null;

        try
        {
            services = SdmBootstrapper.CreateServiceProvider();
            ILogger logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("SDM.Startup");
            IApplicationInfoService appInfo = services.GetRequiredService<IApplicationInfoService>();

            logger.LogInformation("Starting {ApplicationName}.", appInfo.FullName);
            logger.LogInformation("Application version: {ApplicationVersion}.", appInfo.Version);
            logger.LogInformation(
                "Writing diagnostics to {LogDirectory}.",
                services.GetRequiredService<FileLoggerProvider>().Directory);
            logger.LogInformation("User settings file: {SettingsPath}.", SdmBootstrapper.UserSettingsPath);

            BuildAvaloniaApp(services).StartWithClassicDesktopLifetime(args);

            logger.LogInformation("Application shutdown completed.");
            return 0;
        }
        catch (Exception exception)
        {
            // The container may be the very thing that failed, so this cannot rely on it.
            // Without a console to print to, a file is the only place a startup crash can
            // leave a trace the user is able to find.
            string report = CrashLog.Write(exception);

            services?.GetService<ILoggerFactory>()?
                .CreateLogger("SDM.Startup")
                .LogCritical(exception, "Unexpected startup failure.");

            Console.Error.WriteLine($"SDM failed to start: {exception.Message}");
            Console.Error.WriteLine($"Details written to {report}");
            return 1;
        }
        finally
        {
            // DisposeAsync, not Dispose: the container now holds services that only
            // implement IAsyncDisposable — the browser bridge has a loop to unwind —
            // and disposing such a container synchronously throws.
            if (services is not null)
            {
                await services.DisposeAsync();
            }
        }
    }

    private static AppBuilder BuildAvaloniaApp(IServiceProvider services)
    {
        return AppBuilder.Configure(() => new App(services))
            .UsePlatformDetect()
            .LogToTrace();
    }
}
