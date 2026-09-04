using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;
using SDM.Core.Downloads;

namespace SDM.Infrastructure.Downloads;

/// <summary>
/// Downloads several byte ranges of one file at the same time, each connection writing
/// straight into its own region of a single pre-allocated file. The gain is not a faster
/// line: it is that many servers throttle per connection, and that one TCP stream cannot
/// fill a high-latency link on its own.
/// </summary>
internal sealed class SegmentedTransfer
{
    private const int BufferSize = 81920;
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(2);

    private readonly Lock _sync = new();
    private readonly SegmentState[] _segments;
    private readonly string _partialPath;
    private readonly PartialFileMetadata _metadata;
    private readonly IProgress<DownloadProgress>? _progress;
    private readonly IProgress<IReadOnlyList<SegmentProgress>>? _segmentProgress;
    private readonly long _totalBytes;

    private readonly Stopwatch _sinceLastReport = Stopwatch.StartNew();
    private readonly Stopwatch _sinceLastCheckpoint = Stopwatch.StartNew();

    private long _bytesWritten;

    public SegmentedTransfer(
        SegmentState[] segments,
        string partialPath,
        PartialFileMetadata metadata,
        long totalBytes,
        IProgress<DownloadProgress>? progress,
        IProgress<IReadOnlyList<SegmentProgress>>? segmentProgress = null)
    {
        _segmentProgress = segmentProgress;
        _segments = segments;
        _partialPath = partialPath;
        _metadata = metadata;
        _totalBytes = totalBytes;
        _progress = progress;
        _bytesWritten = segments.Sum(segment => segment.Completed);
    }

    /// <summary>
    /// Splits <paramref name="totalBytes"/> into <paramref name="count"/> contiguous
    /// ranges. The last one absorbs the remainder so no byte is left unclaimed.
    /// </summary>
    public static SegmentState[] Split(long totalBytes, int count)
    {
        long size = totalBytes / count;
        SegmentState[] segments = new SegmentState[count];

        for (int index = 0; index < count; index++)
        {
            long start = index * size;
            long end = index == count - 1 ? totalBytes - 1 : start + size - 1;
            segments[index] = new SegmentState(start, end, 0);
        }

        return segments;
    }

    /// <summary>
    /// Runs every unfinished segment. <paramref name="firstResponse"/> is the connection
    /// that was already opened to discover the file's size; reusing it for segment zero
    /// saves a round trip and a connection.
    /// </summary>
    public async Task<long> RunAsync(
        SafeFileHandle handle,
        HttpResponseMessage? firstResponse,
        Func<SegmentState, CancellationToken, Task<HttpResponseMessage>> openSegment,
        Action onDataReceived,
        CancellationToken cancellationToken)
    {
        List<Task> running = [];

        for (int index = 0; index < _segments.Length; index++)
        {
            if (_segments[index].IsComplete)
            {
                continue;
            }

            int captured = index;
            HttpResponseMessage? reuse = captured == 0 ? firstResponse : null;

            running.Add(RunSegmentAsync(captured, handle, reuse, openSegment, onDataReceived, cancellationToken));
        }

        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
        finally
        {
            // Whatever happened, record how far each segment got: this is what lets a
            // killed process pick the transfer back up.
            Checkpoint();
        }

        return _bytesWritten;
    }

    private async Task RunSegmentAsync(
        int index,
        SafeFileHandle handle,
        HttpResponseMessage? reuse,
        Func<SegmentState, CancellationToken, Task<HttpResponseMessage>> openSegment,
        Action onDataReceived,
        CancellationToken cancellationToken)
    {
        SegmentState segment = _segments[index];

        HttpResponseMessage response =
            reuse ?? await openSegment(segment, cancellationToken).ConfigureAwait(false);

        try
        {
            // Before the stream is even opened, and so before a single byte can be
            // written at this segment's offset.
            EnsureAnswersTheRange(response, segment);

            await using Stream source =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[BufferSize];
            long position = segment.Position;
            long remaining = segment.Length - segment.Completed;

            while (remaining > 0)
            {
                int wanted = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);

                if (read <= 0)
                {
                    break;
                }

                // Positional writes on a shared handle are safe because segments never
                // overlap, and they avoid a lock around the file for every chunk.
                await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), position, cancellationToken).ConfigureAwait(false);

                position += read;
                remaining -= read;
                onDataReceived();

                Advance(index, read);
            }

            if (remaining > 0)
            {
                throw new DownloadFailedException(
                    $"The connection closed with {remaining} bytes of this part still missing.",
                    statusCode: null,
                    retryAfter: null,
                    isTransient: true);
            }
        }
        finally
        {
            response.Dispose();
        }
    }

    /// <summary>
    /// Proves the response really is the byte range this segment asked for.
    ///
    /// A segment writes straight to its own offset and stops reading after its own
    /// length, so a server that answers 200 with the whole file — or 206 with a
    /// different range — has the beginning of the file copied into the middle of the
    /// download, and the loop below finishes without complaining. Nothing downstream
    /// catches it either: the byte count and the file's length both come out exactly
    /// right, so verification passes, the partial file is deleted, and the transfer is
    /// reported as finished. The first sign of trouble would be the user opening a
    /// corrupt archive weeks later.
    ///
    /// Ranges are optional in HTTP, so this is not hypothetical. Segmenting is only
    /// chosen after one 206, and a host answered by several machines can honour a range
    /// on that connection and ignore it on the next.
    ///
    /// Transient, because nothing has been written: the partial file is still exactly as
    /// correct as it was, and the retry asks for the same range again.
    /// </summary>
    private static void EnsureAnswersTheRange(HttpResponseMessage response, SegmentState segment)
    {
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new DownloadFailedException(
                $"The server answered {(int)response.StatusCode} {response.StatusCode} instead of the "
                + $"{segment.Position}–{segment.End} byte range this part asked for.",
                (int)response.StatusCode,
                retryAfter: null,
                isTransient: true);
        }

        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;

        // Only where it starts is compared. The first response is reused for segment zero
        // and was opened without an end — "bytes=0-", the request that discovered the file
        // could be split at all — so its range legitimately runs past this segment. Where
        // it ends is enforced by the read loop, which stops at the segment's own length
        // and treats anything short as a failure.
        if (range is not { HasRange: true } || range.From != segment.Position)
        {
            throw new DownloadFailedException(
                $"This part asked to start at byte {segment.Position} and the server sent "
                + $"{range?.ToString() ?? "no range at all"}.",
                (int)response.StatusCode,
                retryAfter: null,
                isTransient: true);
        }
    }

    private void Advance(int index, int bytes)
    {
        bool checkpoint;

        lock (_sync)
        {
            _segments[index] = _segments[index] with { Completed = _segments[index].Completed + bytes };
            _bytesWritten += bytes;

            if (_sinceLastReport.Elapsed >= ProgressInterval)
            {
                _progress?.Report(new DownloadProgress(_bytesWritten, _totalBytes));
                _segmentProgress?.Report(Snapshot());
                _sinceLastReport.Restart();
            }

            checkpoint = _sinceLastCheckpoint.Elapsed >= CheckpointInterval;

            if (checkpoint)
            {
                _sinceLastCheckpoint.Restart();
            }
        }

        if (checkpoint)
        {
            Checkpoint();
        }
    }

    /// <summary>A copy safe to hand to the interface while segments keep advancing.</summary>
    private IReadOnlyList<SegmentProgress> Snapshot()
    {
        SegmentProgress[] snapshot = new SegmentProgress[_segments.Length];

        for (int index = 0; index < _segments.Length; index++)
        {
            SegmentState segment = _segments[index];
            snapshot[index] = new SegmentProgress(
                index + 1, segment.Start, segment.End, segment.Completed);
        }

        return snapshot;
    }

    /// <summary>Writes segment positions to disk so an interrupted split transfer can resume.</summary>
    private void Checkpoint()
    {
        SegmentState[] snapshot;

        lock (_sync)
        {
            snapshot = [.. _segments];
        }

        PartialFile.Write(_partialPath, _metadata with { Segments = snapshot });
    }
}
