using System.Text.Json;
using SDM.Application.Integration;
using SDM.Infrastructure.Logging;

namespace SDM.NativeHost;

/// <summary>
/// The bridge Chrome launches. It reads framed JSON from stdin, hands each request to the
/// running SDM over a named pipe, and writes the answer back — and writes nothing else to
/// stdout ever, because the browser reads that stream as a length-prefixed protocol.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            return await SelfTest.RunAsync();
        }

        NativeMessagingChannel channel = new(
            Console.OpenStandardInput(), Console.OpenStandardOutput());

        BridgeClient client = new();

        try
        {
            while (true)
            {
                string? request = await channel.ReadAsync();

                if (request is null)
                {
                    // The browser closed the pipe: the extension was disabled, or the
                    // browser is shutting down. Leaving quietly is the correct end.
                    return 0;
                }

                await channel.WriteAsync(await HandleAsync(request, client));
            }
        }
        catch (Exception exception)
        {
            // Diagnostics go to a file. Writing them to stderr would be harmless, but
            // writing them to stdout would corrupt the protocol permanently, and the two
            // are easy to confuse under pressure.
            CrashLog.Write(exception);
            return 1;
        }
    }

    private static async Task<BridgeReply> HandleAsync(string request, BridgeClient client)
    {
        BridgeMessage? message;

        try
        {
            message = JsonSerializer.Deserialize<BridgeMessage>(
                request, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch (JsonException)
        {
            return BridgeReply.Failed("The request was not valid JSON.");
        }

        if (message is null)
        {
            return BridgeReply.Failed("The request was empty.");
        }

        try
        {
            return await client.SendAsync(message);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException)
        {
            // An answer always goes back, even a failing one: the extension is waiting on
            // this stream and silence would hang it.
            return BridgeReply.Failed(exception.Message);
        }
    }
}
