using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SDM.Application.Settings;
using SDM.Infrastructure;
using SDM.Infrastructure.Settings;

namespace SDM.IntegrationTests.Settings;

public sealed class JsonUserSettingsStoreTests : IDisposable
{
    // A temporary path, never the real per-user file: two test classes sharing one real
    // file fight each other when xUnit runs them at the same time.
    private readonly string _directory = Directory.CreateTempSubdirectory("sdm-store-").FullName;

    private string Path => System.IO.Path.Combine(_directory, "settings.json");

    [Fact]
    public void Path_DefaultsToOutsideTheInstallationFolder()
    {
        JsonUserSettingsStore store = new(NullLogger<JsonUserSettingsStore>.Instance);

        Assert.Equal(SdmPaths.UserSettingsPath, store.Path);
        Assert.DoesNotContain(AppContext.BaseDirectory, store.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SaveAsync_WritesTheConfigurationShapeTheApplicationReadsBack()
    {
        await Create().SaveAsync(
            new UserSettings
            {
                DownloadFolder = @"D:\Downloads",
                AskWhereToSave = true,
                MaximumSegments = 8,
            },
            TestContext.Current.CancellationToken);

        JsonObject downloads = Assert.IsType<JsonObject>(Read()["Downloads"]);

        Assert.Equal(@"D:\Downloads", (string?)downloads["Folder"]);
        Assert.True((bool?)downloads["AskWhereToSave"]);
        Assert.Equal(8, (int?)downloads["MaximumSegments"]);
    }

    [Fact]
    public async Task SaveAsync_LeavesSectionsItDoesNotManageAlone()
    {
        // The file belongs to the user: a hand-written FileLog section must survive the
        // settings screen saving something else.
        await File.WriteAllTextAsync(
            Path,
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
        await File.WriteAllTextAsync(Path, "{ this is not json", TestContext.Current.CancellationToken);

        await Create().SaveAsync(new UserSettings(), TestContext.Current.CancellationToken);

        Assert.NotNull(Read()["Downloads"]);
    }

    [Fact]
    public async Task SaveAsync_CreatesTheFolderWhenItIsNotThereYet()
    {
        string nested = System.IO.Path.Combine(_directory, "nested", "settings.json");
        JsonUserSettingsStore store = new(NullLogger<JsonUserSettingsStore>.Instance, nested);

        await store.SaveAsync(new UserSettings(), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(nested));
    }

    private JsonUserSettingsStore Create() =>
        new(NullLogger<JsonUserSettingsStore>.Instance, Path);

    private JsonObject Read() =>
        JsonNode.Parse(File.ReadAllText(Path)) as JsonObject
        ?? throw new JsonException("The settings file did not contain an object.");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory must not fail an otherwise passing test.
        }
    }
}
