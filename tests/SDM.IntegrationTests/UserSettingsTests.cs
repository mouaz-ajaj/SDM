using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Desktop;

namespace SDM.IntegrationTests;

public sealed class UserSettingsTests
{
    [Fact]
    public void UserSettingsPath_LivesOutsideTheInstallation()
    {
        // A preference written next to the executable is replaced by the next build or
        // update, so it must not live there.
        string path = SdmBootstrapper.UserSettingsPath;

        Assert.EndsWith("settings.json", path, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SDM", path, StringComparison.Ordinal);
    }

    [Fact]
    public void UserSettings_OverrideTheShippedDefaults()
    {
        string directory = Directory.CreateTempSubdirectory("sdm-settings-").FullName;

        try
        {
            // Only the one key the user changed; everything else must still come from
            // the shipped appsettings.json.
            File.WriteAllText(
                Path.Combine(directory, "appsettings.json"),
                JsonSerializer.Serialize(new
                {
                    Application = new { Name = "SDM", FullName = "Speed Download Manager" },
                    Downloads = new { AskWhereToSave = true },
                }));

            using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider(directory);
            DownloadOptions options = provider.GetRequiredService<IOptions<DownloadOptions>>().Value;

            Assert.True(options.AskWhereToSave);
            Assert.Equal(3, options.MaximumConcurrent);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temp directory must not fail an otherwise passing test.
            }
        }
    }
}
