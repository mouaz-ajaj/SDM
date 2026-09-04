using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;
using SDM.Infrastructure.Integration;

namespace SDM.IntegrationTests.Integration;

/// <summary>
/// Registration writes to the registry, so these tests write to a subtree of their own.
/// A test that quietly rewrote the key a real Chrome reads would break the machine it ran
/// on, and would pass while doing it.
///
/// Windows only, like the registration itself and like the continuous integration that
/// runs them.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BrowserRegistrationTests : IDisposable
{
    private readonly string _registryRoot = @"Software\SDM.Tests\" + Guid.NewGuid().ToString("N");
    private readonly string _hostPath = Path.Combine(Path.GetTempPath(), $"sdm-host-{Guid.NewGuid():N}.exe");

    public BrowserRegistrationTests()
    {
        File.WriteAllBytes(_hostPath, []);
    }

    private static BrowserTarget Chrome => BrowserRegistration.KnownBrowsers[0];

    [Fact]
    public void Register_PointsTheBrowserAtTheManifest()
    {
        IReadOnlyList<BrowserTarget> registered =
            BrowserRegistration.Register(_hostPath, [Chrome], _registryRoot);

        Assert.Equal("Google Chrome", Assert.Single(registered).Name);

        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyFor(Chrome));

        // The default value is the whole registration: the browser reads everything else
        // out of the file it names.
        Assert.NotNull(key);
        Assert.Equal(BrowserRegistration.ManifestPath, key.GetValue(null));
    }

    [Fact]
    public void Register_WritesAManifestChromeCanRead()
    {
        BrowserRegistration.Register(_hostPath, [Chrome], _registryRoot);

        byte[] raw = File.ReadAllBytes(BrowserRegistration.ManifestPath);

        // No byte order mark. Windows PowerShell writes one by default, and three stray
        // bytes in front of a JSON document are three bytes Chrome's parser has no idea
        // what to do with — the host does not register and the browser says only "host
        // not found". This is why the manifest is written here rather than by a script.
        Assert.False(
            raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF,
            "the manifest starts with a UTF-8 byte order mark");

        using JsonDocument manifest = JsonDocument.Parse(raw);
        JsonElement root = manifest.RootElement;

        Assert.Equal(BrowserRegistration.HostName, root.GetProperty("name").GetString());
        Assert.Equal("stdio", root.GetProperty("type").GetString());
        Assert.Equal(Path.GetFullPath(_hostPath), root.GetProperty("path").GetString());

        // Chrome refuses every caller that is not named here, and reports it as "host not
        // found" — the same message as a missing registration, for a different reason.
        Assert.Equal(
            $"chrome-extension://{BrowserRegistration.ExtensionId}/",
            Assert.Single(root.GetProperty("allowed_origins").EnumerateArray()).GetString());
    }

    [Fact]
    public void Register_RefusesAHostThatIsNotThere()
    {
        // Registering a path with nothing at it produces a machine that looks correctly
        // set up and cannot work — which is the hardest kind of fault to find, because
        // the registry value reads exactly right.
        Assert.Throws<FileNotFoundException>(
            () => BrowserRegistration.Register(
                Path.Combine(Path.GetTempPath(), "no-such-host.exe"), [Chrome], _registryRoot));
    }

    [Fact]
    public void Unregister_RemovesTheKeyAndTheManifest()
    {
        BrowserRegistration.Register(_hostPath, [Chrome], _registryRoot);
        BrowserRegistration.Unregister([Chrome], _registryRoot);

        Assert.Null(Registry.CurrentUser.OpenSubKey(KeyFor(Chrome)));
        Assert.False(File.Exists(BrowserRegistration.ManifestPath));
    }

    [Fact]
    public void Unregister_IsQuietWhenThereWasNothingToRemove()
    {
        // The uninstaller runs this whether or not registration ever happened.
        Assert.Null(Record.Exception(
            () => BrowserRegistration.Unregister(BrowserRegistration.KnownBrowsers, _registryRoot)));
    }

    [Fact]
    public void Detect_FindsOnlyBrowsersWithAProfileFolder()
    {
        BrowserTarget imaginary = new("Nothing", @"Software\Nothing", @"Nothing\User Data");

        // An installed browser nobody has opened has no profile folder, and registering a
        // host for it is clutter in someone else's registry.
        Assert.Empty(BrowserRegistration.Detect([imaginary]));
    }

    [Fact]
    public void KnownBrowsers_AreDistinctAndCompletelyDescribed()
    {
        Assert.NotEmpty(BrowserRegistration.KnownBrowsers);

        Assert.All(BrowserRegistration.KnownBrowsers, browser =>
        {
            Assert.False(string.IsNullOrWhiteSpace(browser.Name));
            Assert.StartsWith(@"Software\", browser.RegistryPath, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(browser.UserDataPath));
        });

        Assert.Equal(
            BrowserRegistration.KnownBrowsers.Count,
            BrowserRegistration.KnownBrowsers.Select(browser => browser.RegistryPath).Distinct().Count());
    }

    private string KeyFor(BrowserTarget browser) =>
        $@"{_registryRoot}\{browser.RegistryPath}\NativeMessagingHosts\{BrowserRegistration.HostName}";

    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                Registry.CurrentUser.DeleteSubKeyTree(_registryRoot, throwOnMissingSubKey: false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A leftover test key under Software\SDM.Tests is harmless.
            }
        }

        try
        {
            File.Delete(_hostPath);
            File.Delete(BrowserRegistration.ManifestPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Same.
        }
    }
}
