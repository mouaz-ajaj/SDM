using SDM.Infrastructure.Integration;

namespace SDM.NativeHost;

/// <summary>
/// The installer's entry into browser registration.
///
/// Everything is reported on stderr, never stdout, for the same reason the rest of this
/// executable is careful about it: the browser reads stdout as a length-prefixed protocol,
/// and one stray line would desynchronise it permanently. That the installer is the only
/// thing that ever passes these arguments is not a reason to make the exception.
/// </summary>
internal static class Registration
{
    public static int Register()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("Browser registration is only implemented for Windows.");
            return 1;
        }

        // Beside this executable, because that is where the installer put both of them and
        // where the browser will be told to look.
        string hostPath = Path.Combine(AppContext.BaseDirectory, "SDM.NativeHost.exe");

        IReadOnlyList<BrowserTarget> found = BrowserRegistration.Detect();

        if (found.Count == 0)
        {
            // Not a failure. Somebody may install SDM before the browser they intend to
            // use it with, and failing the installation over that would be absurd.
            Console.Error.WriteLine("No Chromium browser was found. Run --register again after installing one.");
            return 0;
        }

        try
        {
            IReadOnlyList<BrowserTarget> registered = BrowserRegistration.Register(hostPath, found);

            foreach (BrowserTarget browser in registered)
            {
                Console.Error.WriteLine($"registered  {browser.Name}");
            }

            Console.Error.WriteLine($"manifest    {BrowserRegistration.ManifestPath}");

            return registered.Count > 0 ? 0 : 1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine("Could not register the browser bridge: " + exception.Message);
            return 1;
        }
    }

    public static int Unregister()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        // Every known browser, not only the ones still detected: a browser uninstalled
        // since SDM was set up would otherwise leave its key behind for ever.
        BrowserRegistration.Unregister(BrowserRegistration.KnownBrowsers);
        Console.Error.WriteLine("The browser bridge was removed.");

        return 0;
    }
}
