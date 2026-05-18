namespace JackcessDotNet;

/// <summary>
/// Bitmask indicating which columns are NOT NULL. For boolean columns, a set bit means true.
/// </summary>
internal sealed class NullMask
{
    private readonly int _columnCount;
    private readonly byte[] _mask;

    public NullMask(int columnCount)
    {
        _columnCount = columnCount;
        _mask = new byte[(columnCount + 7) / 8];
    }

    public int ByteSize => _mask.Length;

    public void MarkNotNull(int columnNumber)
    {
        if (columnNumber >= _columnCount)
            return;
        int idx = columnNumber / 8;
        int bit = columnNumber % 8;
        _mask[idx] = (byte)(_mask[idx] | (1 << bit));
    }

    public void WriteTo(byte[] buffer, int offset)
        => Array.Copy(_mask, 0, buffer, offset, _mask.Length);
}
