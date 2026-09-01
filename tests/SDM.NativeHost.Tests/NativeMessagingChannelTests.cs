using System.Buffers.Binary;
using System.Text;
using SDM.Application.Integration;
using SDM.NativeHost;

namespace SDM.NativeHost.Tests;

public sealed class NativeMessagingChannelTests
{
    [Fact]
    public async Task WriteAsync_PrefixesTheLengthAsFourLittleEndianBytes()
    {
        using MemoryStream buffer = new();

        await new NativeMessagingChannel(Stream.Null, buffer).WriteAsync("hi", TestContext.Current.CancellationToken);

        byte[] written = buffer.ToArray();

        // Chrome reads the length first and byte order is not negotiable.
        Assert.Equal(6, written.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(written));
        Assert.Equal("hi", Encoding.UTF8.GetString(written.AsSpan(4)));
    }

    [Fact]
    public async Task ReadAsync_RoundTripsAMessage()
    {
        using MemoryStream buffer = new();
        await new NativeMessagingChannel(Stream.Null, buffer)
            .WriteAsync("""{"type":"ping"}""", TestContext.Current.CancellationToken);

        buffer.Position = 0;

        Assert.Equal("""{"type":"ping"}""", await new NativeMessagingChannel(buffer, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_HandlesNonAsciiWithoutTruncating()
    {
        // The length is a byte count, not a character count: a file name in Arabic or an
        // emoji in a title is where a character-counting implementation breaks.
        const string Message = """{"fileName":"تقرير ربع سنوي.pdf"}""";

        using MemoryStream buffer = new();
        await new NativeMessagingChannel(Stream.Null, buffer).WriteAsync(Message, TestContext.Current.CancellationToken);
        buffer.Position = 0;

        Assert.Equal(Message, await new NativeMessagingChannel(buffer, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ReassemblesAMessageDeliveredInPieces()
    {
        // A pipe delivers what it has, not what was asked for. Treating a short read as
        // the whole message is the classic native messaging bug.
        byte[] framed = Frame("""{"type":"download"}""");

        using DribblingStream input = new(framed, bytesPerRead: 3);

        Assert.Equal("""{"type":"download"}""", await new NativeMessagingChannel(input, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_ReturnsNullWhenTheBrowserClosesTheStream()
    {
        // Not an error: the extension was disabled, or the browser is shutting down.
        Assert.Null(await new NativeMessagingChannel(Stream.Null, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RefusesAnImpossiblyLargeMessage()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, uint.MaxValue);

        using MemoryStream input = new(header);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new NativeMessagingChannel(input, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_RejectsAMessageShorterThanItsAnnouncedLength()
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 100);

        using MemoryStream input = new([.. header, .. "short"u8.ToArray()]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new NativeMessagingChannel(input, Stream.Null).ReadAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Channel_CarriesABridgeMessageIntactBothWays()
    {
        BridgeMessage sent = new()
        {
            Type = BridgeProtocol.Download,
            Url = "https://example.test/file.bin",
            FileName = "file.bin",
        };

        using MemoryStream buffer = new();
        await new NativeMessagingChannel(Stream.Null, buffer).WriteAsync(sent, TestContext.Current.CancellationToken);
        buffer.Position = 0;

        string? read = await new NativeMessagingChannel(buffer, Stream.Null).ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(read);
        Assert.Contains("\"url\":\"https://example.test/file.bin\"", read, StringComparison.Ordinal);
        Assert.Contains("\"type\":\"download\"", read, StringComparison.Ordinal);
    }

    private static byte[] Frame(string message)
    {
        byte[] payload = Encoding.UTF8.GetBytes(message);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)payload.Length);

        return [.. header, .. payload];
    }

    /// <summary>A stream that hands over only a few bytes at a time, the way a pipe does.</summary>
    private sealed class DribblingStream(byte[] content, int bytesPerRead) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int available = Math.Min(Math.Min(bytesPerRead, count), content.Length - _position);

            Array.Copy(content, _position, buffer, offset, available);
            _position += available;

            return available;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
