using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SDM.NativeHost;

/// <summary>
/// Chrome's native messaging framing: a 4-byte little-endian length, then that many bytes
/// of UTF-8 JSON. The browser reads the length first, so a single stray byte on stdout —
/// a log line, a warning, a stack trace — is read as a length and desynchronises the
/// stream permanently. Nothing else may ever write to this stream.
/// </summary>
public sealed class NativeMessagingChannel
{
    /// <summary>Chrome refuses anything larger, so accepting more would only defer the failure.</summary>
    public const int MaximumMessageBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Stream _input;
    private readonly Stream _output;

    public NativeMessagingChannel(Stream input, Stream output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        _input = input;
        _output = output;
    }

    /// <summary>Reads one message, or null when the browser has closed the pipe.</summary>
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];

        if (!await ReadExactlyAsync(header, cancellationToken))
        {
            return null;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);

        if (length == 0)
        {
            return string.Empty;
        }

        if (length > MaximumMessageBytes)
        {
            throw new InvalidDataException(
                $"The browser announced a {length} byte message, beyond the {MaximumMessageBytes} byte limit.");
        }

        byte[] payload = new byte[length];

        if (!await ReadExactlyAsync(payload, cancellationToken))
        {
            throw new InvalidDataException("The message ended before its announced length.");
        }

        return Encoding.UTF8.GetString(payload);
    }

    public async Task WriteAsync(string json, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(json);

        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        await _output.WriteAsync(header, cancellationToken);
        await _output.WriteAsync(payload, cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    public Task WriteAsync<T>(T value, CancellationToken cancellationToken = default) =>
        WriteAsync(JsonSerializer.Serialize(value, Json), cancellationToken);

    /// <summary>
    /// A pipe delivers what it has, not what was asked for, so a partial read is normal
    /// rather than an error — treating it as one is the classic native messaging bug.
    /// </summary>
    private async Task<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int filled = 0;

        while (filled < buffer.Length)
        {
            int read = await _input.ReadAsync(buffer.AsMemory(filled), cancellationToken);

            if (read == 0)
            {
                return false;
            }

            filled += read;
        }

        return true;
    }
}
