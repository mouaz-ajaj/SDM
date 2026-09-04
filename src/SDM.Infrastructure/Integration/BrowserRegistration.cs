using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace SDM.Infrastructure.Integration;

/// <summary>A Chromium browser SDM can register its native messaging host with.</summary>
/// <param name="Name">What to call it on screen.</param>
/// <param name="RegistryPath">Under HKCU, where that browser looks for host manifests.</param>
/// <param name="UserDataPath">
/// The profile folder, relative to LocalApplicationData. Its existence is the difference
/// between a browser that is installed and one that is actually used — and registering a
/// host for a browser nobody opens is clutter in someone else's registry.
/// </param>
public sealed record BrowserTarget(string Name, string RegistryPath, string UserDataPath);

/// <summary>
/// Registers SDM's native messaging host with the browsers on this machine.
///
/// In the application rather than in the installer, and in C# rather than in PowerShell,
/// for two reasons that are not a matter of taste. Script execution is disabled by policy
/// on a great many Windows machines — including the one this was written on — so an
/// installer that shells out to a .ps1 fails on exactly the users least able to diagnose
/// it. And the manifest holds absolute paths: a user named in Arabic, Chinese or Cyrillic
/// has a profile folder that an installer script's ANSI file writing turns into nonsense.
///
/// Everything here is per user. Nothing needs administrator rights, nothing is written
/// outside HKCU and the user's own data folder, and uninstalling removes exactly what
/// installing added.
/// </summary>
[SupportedOSPlatform("windows")]
public static class BrowserRegistration
{
    /// <summary>The name the extension and the host manifest agree on.</summary>
    public const string HostName = "com.sdm.host";

    /// <summary>
    /// The extension's id, fixed by the public key in its manifest.
    ///
    /// Chrome would otherwise derive an unpacked extension's id from the folder it was
    /// loaded from, so moving or reinstalling SDM would change it and silently break the
    /// connection — with the browser reporting only "host not found". The key pins it.
    /// </summary>
    public const string ExtensionId = "efcijjodjgojhelobljfkbigkndfeobe";

    /// <summary>
    /// Every Chromium browser worth trying. Firefox is absent on purpose: it keeps its
    /// host manifests elsewhere and describes them differently, and shipping a manifest
    /// it cannot read would be worse than not registering at all.
    /// </summary>
    public static IReadOnlyList<BrowserTarget> KnownBrowsers { get; } =
    [
        new("Google Chrome", @"Software\Google\Chrome", @"Google\Chrome\User Data"),
        new("Microsoft Edge", @"Software\Microsoft\Edge", @"Microsoft\Edge\User Data"),
        new("Brave", @"Software\BraveSoftware\Brave-Browser", @"BraveSoftware\Brave-Browser\User Data"),
        new("Vivaldi", @"Software\Vivaldi", @"Vivaldi\User Data"),
        new("Opera", @"Software\Opera Software", @"Opera Software\Opera Stable"),
        new("Chromium", @"Software\Chromium", @"Chromium\User Data"),
    ];

    /// <summary>Where the host manifest lives.</summary>
    /// <remarks>
    /// Beside the user's data, not beside the executable. The executable's folder is a
    /// build output during development and an installation folder afterwards, and both get
    /// replaced — taking the manifest with them while the registry entry stays behind
    /// pointing at a file that is no longer there. The browser then reports "host not
    /// found" while the registration looks perfectly correct.
    /// </remarks>
    public static string ManifestPath => Path.Combine(SdmPaths.UserDataDirectory, HostName + ".json");

    /// <summary>
    /// The browsers this machine actually uses: a profile folder proves somebody has
    /// opened it at least once, which an installation on its own does not.
    /// </summary>
    public static IReadOnlyList<BrowserTarget> Detect(IEnumerable<BrowserTarget>? among = null)
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        return
        [
            .. (among ?? KnownBrowsers).Where(browser =>
                !string.IsNullOrEmpty(local)
                && Directory.Exists(Path.Combine(local, browser.UserDataPath)))
        ];
    }

    /// <summary>
    /// Writes the host manifest and points every given browser at it. Returns the ones
    /// that took.
    /// </summary>
    /// <param name="hostPath">The native messaging host executable.</param>
    /// <param name="registryRoot">
    /// Prefixed to each browser's key. Empty in production; tests pass their own so that
    /// running them never touches the registry a real browser reads.
    /// </param>
    public static IReadOnlyList<BrowserTarget> Register(
        string hostPath, IEnumerable<BrowserTarget> browsers, string registryRoot = "")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);
        ArgumentNullException.ThrowIfNull(browsers);

        if (!File.Exists(hostPath))
        {
            throw new FileNotFoundException("The native messaging host was not found.", hostPath);
        }

        WriteManifest(Path.GetFullPath(hostPath));

        List<BrowserTarget> registered = [];

        foreach (BrowserTarget browser in browsers)
        {
            try
            {
                using RegistryKey key = Registry.CurrentUser.CreateSubKey(
                    KeyFor(registryRoot, browser), writable: true);

                // The default value is the whole registration: it is the path to the
                // manifest, and the browser reads everything else from there.
                key.SetValue(null, ManifestPath, RegistryValueKind.String);
                registered.Add(browser);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // One browser that will not take the key must not stop the others. A
                // partially registered machine still works for the browsers that took.
            }
        }

        return registered;
    }

    /// <summary>Removes what <see cref="Register"/> added, and nothing else.</summary>
    public static void Unregister(IEnumerable<BrowserTarget> browsers, string registryRoot = "")
    {
        ArgumentNullException.ThrowIfNull(browsers);

        foreach (BrowserTarget browser in browsers)
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(KeyFor(registryRoot, browser), throwOnMissingSubKey: false);
            }
            catch (Exception exception) when (
                exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Best effort. A key left behind points at a manifest that is about to go,
                // which the browser treats the same way as no key at all.
            }
        }

        try
        {
            File.Delete(ManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same.
        }
    }

    private static string KeyFor(string registryRoot, BrowserTarget browser) =>
        string.IsNullOrEmpty(registryRoot)
            ? $@"{browser.RegistryPath}\NativeMessagingHosts\{HostName}"
            : $@"{registryRoot}\{browser.RegistryPath}\NativeMessagingHosts\{HostName}";

    /// <summary>
    /// The manifest, as Chrome's native messaging documentation describes it.
    ///
    /// Written as UTF-8 with no byte order mark, which is not a detail. Windows PowerShell
    /// writes one by default, and three stray bytes in front of a JSON document are three
    /// bytes Chrome's parser has no idea what to do with: the host does not register, and
    /// the browser says only "host not found".
    /// </summary>
    private static void WriteManifest(string hostPath)
    {
        SdmPaths.EnsureUserDataDirectory();

        string json = JsonSerializer.Serialize(
            new
            {
                name = HostName,
                description = "Speed Download Manager bridge",
                path = hostPath,
                type = "stdio",
                allowed_origins = new[] { $"chrome-extension://{ExtensionId}/" },
            },
            new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(ManifestPath, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}
