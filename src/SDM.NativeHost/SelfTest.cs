using System.Text.Json;
using SDM.Application.Integration;

namespace SDM.NativeHost;

/// <summary>
/// Exercises the host without a browser. Results go to stderr, never stdout, so the check
/// itself cannot be the thing that corrupts the protocol it is checking.
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        bool framing = await CheckFramingAsync();
        bool reachable = await CheckApplicationAsync();

        Report("Message framing", framing);

        // A missing application is not a failure of the host: the bridge is installed
        // correctly, SDM simply is not running yet. Reporting that as "FAIL" made a sound
        // installation look broken, which is the opposite of what a self test is for.
        Console.Error.WriteLine(reachable
            ? "ok    SDM reachable"
            : "--    SDM is not running. The bridge is installed; start SDM and it will answer.");

        return framing ? 0 : 1;
    }

    private static async Task<bool> CheckFramingAsync()
    {
        BridgeMessage sent = new()
        {
            Type = BridgeProtocol.Download,
            Url = "https://example.test/file.bin",
        };

        using MemoryStream buffer = new();

        await new NativeMessagingChannel(Stream.Null, buffer).WriteAsync(sent);
        buffer.Position = 0;

        string? read = await new NativeMessagingChannel(buffer, Stream.Null).ReadAsync();

        BridgeMessage? roundTripped = read is null
            ? null
            : JsonSerializer.Deserialize<BridgeMessage>(
                read, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return roundTripped?.Url == sent.Url;
    }

    private static async Task<bool> CheckApplicationAsync()
    {
        try
        {
            // Never start SDM from a self test: checking whether something is running
            // must not be the thing that makes it run.
            BridgeClient client = new(
                connectTimeout: TimeSpan.FromMilliseconds(500),
                startApplication: () => false);

            BridgeReply reply = await client.SendAsync(new BridgeMessage { Type = BridgeProtocol.Ping });

            return string.Equals(reply.Type, BridgeProtocol.Pong, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            return false;
        }
    }

    private static void Report(string check, bool passed) =>
        Console.Error.WriteLine($"{(passed ? "ok  " : "FAIL")}  {check}");
}
