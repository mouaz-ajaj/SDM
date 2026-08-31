namespace SDM.Core.Downloads;

/// <summary>
/// How far one connection of a split transfer has got. Reported separately from the
/// total because the aggregate number hides the thing worth seeing: whether the parts
/// are keeping pace with each other, or one has stalled while the rest finished.
/// </summary>
public sealed record SegmentProgress(int Index, long Start, long End, long Completed)
{
    public long Length => End - Start + 1;

    public double Percentage => Length > 0 ? (double)Completed / Length * 100d : 0;

    public bool IsComplete => Completed >= Length;
}
