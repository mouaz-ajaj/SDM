using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SDM.Application.Settings;
using SDM.Infrastructure;
using SDM.Infrastructure.Settings;

namespace SDM.IntegrationTests.Settings;

public sealed class JsonUserSettingsStoreTests : IDisposable
{
    private readonly string _path = SdmPaths.UserSettingsPath;
    private readonly string? _original;

    public JsonUserSettingsStoreTests()
    {
        // The store deliberately writes to the real per-user location, so the existing
        // file is put back afterwards rather than clobbered by a test run.
        _original = File.Exists(_path) ? File.ReadAllText(_path) : null;
    }

    [Fact]
    public void Path_IsOutsideTheInstallationFolder()
    {
        JsonUserSettingsStore store = Create();

        Assert.EndsWith("settings.json", store.Path, StringComparison.Ordinal);
        Assert.DoesNotContain(AppContext.BaseDirectory, store.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_WritesTheConfigurationShapeTheApplicationReadsBack()
    {
        JsonUserSettingsStore store = Create();

        await store.SaveAsync(
            new UserSettings
            {
                DownloadFolder = @"D:\Downloads",
                AskWhereToSave = true,
                MaximumSegments = 8,
            },
            TestContext.Current.CancellationToken);

        JsonObject root = Read();
        JsonObject downloads = Assert.IsType<JsonObject>(root["Downloads"]);

        Assert.Equal(@"D:\Downloads", (string?)downloads["Folder"]);
        Assert.True((bool?)downloads["AskWhereToSave"]);
        Assert.Equal(8, (int?)downloads["MaximumSegments"]);
    }

    [Fact]
    public async Task SaveAsync_LeavesSectionsItDoesNotManageAlone()
    {
        // The file belongs to the user: a hand-written FileLog section must survive the
        // settings screen saving something else.
        SdmPaths.EnsureUserDataDirectory();
        await File.WriteAllTextAsync(
            _path,
            """{"FileLog":{"MinimumLevel":"Debug"},"Downloads":{"MaximumConcurrent":9}}""",
            TestContext.Current.CancellationToken);

        await Create().SaveAsync(new UserSettings { AskWhereToSave = true }, TestContext.Current.CancellationToken);

        JsonObject root = Read();

        Assert.Equal("Debug", (string?)root["FileLog"]?["MinimumLevel"]);
        Assert.True((bool?)root["Downloads"]?["AskWhereToSave"]);
    }

    [Fact]
    public async Task SaveAsync_ReplacesAFileItCannotParse()
    {
        SdmPaths.EnsureUserDataDirectory();
        await File.WriteAllTextAsync(_path, "{ this is not json", TestContext.Current.CancellationToken);

        await Create().SaveAsync(new UserSettings(), TestContext.Current.CancellationToken);

        Assert.NotNull(Read()["Downloads"]);
    }

    private static JsonUserSettingsStore Create() => new(NullLogger<JsonUserSettingsStore>.Instance);

    private JsonObject Read() =>
        JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
        ?? throw new JsonException("The settings file did not contain an object.");

    public void Dispose()
    {
        try
        {
            if (_original is null)
            {
                File.Delete(_path);
            }
            else
            {
                File.WriteAllText(_path, _original);
            }
        }
        catch (IOException)
        {
            // Restoring is best effort; failing here would mask the real test result.
        }
    }
}
