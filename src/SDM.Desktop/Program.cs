using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SDM.Application.ApplicationInfo;
using SDM.Infrastructure.Integration;
using SDM.Infrastructure.Logging;

namespace SDM.Desktop;

internal static class Program
{
    [STAThread]
    public static async Task<int> Main(string[] args)
    {
        // Claimed before the container is built, and so before anything opens the
        // database, the log file or the pipe. A second copy that got as far as any of
        // those has already done the damage this is here to prevent.
        using SingleInstance instance = SingleInstance.Claim();

        if (!instance.IsOnly)
        {
            // The copy already running owns everything; this one only asks it to come
            // forward, so a second launch is not a window that never appears.
            await SingleInstance.AskRunningInstanceToShowAsync();
            return 0;
        }

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
