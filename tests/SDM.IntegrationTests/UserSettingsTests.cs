using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SDM.Application.Downloads;
using SDM.Desktop;

namespace SDM.IntegrationTests;

public sealed class UserSettingsTests : IDisposable
{
    private readonly string _shipped = Directory.CreateTempSubdirectory("sdm-shipped-").FullName;
    private readonly string _user = Directory.CreateTempSubdirectory("sdm-user-").FullName;

    public UserSettingsTests() => WriteShipped();

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
        // Only the one key the user changed; everything else must still come from the
        // shipped file.
        File.WriteAllText(
            Path.Combine(_user, "settings.json"),
            """{"Downloads":{"AskWhereToSave":true}}""");

        DownloadOptions options = Build();

        Assert.True(options.AskWhereToSave);
        Assert.Equal(3, options.MaximumConcurrent);
        Assert.Equal(6, options.MaximumConnectionsPerHost);
    }

    [Fact]
    public void ShippedDefaults_ApplyWhenTheUserHasNoSettingsFile()
    {
        DownloadOptions options = Build();

        Assert.False(options.AskWhereToSave);
        Assert.Equal(3, options.MaximumConcurrent);
    }

    private DownloadOptions Build()
    {
        using ServiceProvider provider = SdmBootstrapper.CreateServiceProvider(_shipped, _user);
        return provider.GetRequiredService<IOptionsMonitor<DownloadOptions>>().CurrentValue;
    }

    private void WriteShipped() =>
        File.WriteAllText(
            Path.Combine(_shipped, "appsettings.json"),
            JsonSerializer.Serialize(new
            {
                Application = new { Name = "SDM", FullName = "Speed Download Manager" },
                Downloads = new
                {
                    MaximumConcurrent = 3,
                    MaximumPerHost = 2,
                    MaximumConnectionsPerHost = 6,
                    MaximumSegments = 4,
                    AskWhereToSave = false,
                },
            }));

    public void Dispose()
    {
        foreach (string directory in new[] { _shipped, _user })
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
