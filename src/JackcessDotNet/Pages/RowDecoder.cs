using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Decodes individual column values from raw row bytes produced by <see cref="RowEncoder"/>.
///
/// Row binary layout (Jet4 example):
///   [0..1]              column count (short, SizeRowColumnCount bytes)
///   [2..fixedEnd]       fixed-length column data, laid out in column-number order
///   [fixedEnd..eod-1]   variable-length column data, in var-index order
///   [eod..eod+1]        end-of-data pointer (2 bytes, SizeRowVarColOffset)
///   [..]                variable offsets in REVERSE var-index order (2 bytes each)
///   [..]                variable column count (2 bytes)
///   [..]                null mask (ceil(colCount/8) bytes)
/// </summary>
internal sealed class RowDecoder
{
    private readonly JetFormat              _format;
    private readonly IReadOnlyList<Column>  _columns;
    private readonly LvalReader?            _lvalReader;

    // Precomputed column layout (mirrors RowEncoder)
    private readonly Dictionary<int, int>   _fixedByteOffset = new();  // colNumber → byte offset in fixed area
    private readonly Dictionary<int, int>   _varIndex        = new();  // colNumber → 0-based var index
    private readonly int                    _varCount;

    public RowDecoder(JetFormat format, IReadOnlyList<Column> columns,
                      LvalReader? lvalReader = null)
    {
        _lvalReader = lvalReader;
        _format  = format  ?? throw new ArgumentNullException(nameof(format));
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));

        int fixedOff = 0;
        int varIdx   = 0;
        int maxVar   = 0;

        // Enumerate columns in column-number order to build the layout.
        // Memo/OLE are variable-length (they store an LvRef in the var area), so they
        // are included in varIndex just like Text/Binary.
        foreach (var col in columns.OrderBy(c => c.ColumnNumber))
        {
            if (col.DataType.IsVariableLength())
            {
                // Honour the on-disk var-table index when available so post-deletion
                // tables decode correctly; otherwise fall back to a tight assignment.
                int v = col.VarLenTableIndex >= 0 ? col.VarLenTableIndex : varIdx++;
                _varIndex[col.ColumnNumber] = v;
                if (v + 1 > maxVar) maxVar = v + 1;
            }
            else
            {
                // Same logic for fixed-data offsets.
                int off = col.FixedDataOffset >= 0 ? col.FixedDataOffset : fixedOff;
                _fixedByteOffset[col.ColumnNumber] = off;
                fixedOff = off + col.DataType.GetFixedSize();
            }
        }
        _varCount = maxVar > 0 ? maxVar : varIdx;
    }

    /// <summary>
    /// Decodes the value of <paramref name="col"/> from <paramref name="rowBytes"/>.
    /// Returns <c>null</c> if the column is NULL, deleted, or the row is malformed.
    /// </summary>
    public object? Decode(byte[] rowBytes, Column col)
    {
        if (rowBytes is null || rowBytes.Length < _format.SizeRowColumnCount) return null;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        if (col.ColumnNumber >= colCount) return null;

        // ── Null mask ─────────────────────────────────────────────────────────
        int maskSize   = (colCount + 7) / 8;
        int maskOffset = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) return null;

        bool notNull = (rowBytes[maskOffset + col.ColumnNumber / 8]
                        & (1 << (col.ColumnNumber % 8))) != 0;

        // Boolean: null mask bit IS the value (true/false), never actually null
        if (col.DataType == DataType.Boolean)
            return notNull;

        if (!notNull) return null;

        int fixedBase = _format.SizeRowColumnCount;

        // ── Fixed-length column ───────────────────────────────────────────────
        if (!col.DataType.IsVariableLength())
        {
            if (!_fixedByteOffset.TryGetValue(col.ColumnNumber, out int relOff)) return null;
            int absOff = fixedBase + relOff;
            if (absOff + col.Length > maskOffset) return null;
            return ReadFixed(rowBytes, absOff, col);
        }

        // ── Variable-length column ────────────────────────────────────────────
        if (!_varIndex.TryGetValue(col.ColumnNumber, out int vi)) return null;

        // Trailer layout from the end of the row:
        //   [maskOffset - SizeRowVarColOffset]                = varCount
        //   [maskOffset - (vi+2)*SizeRowVarColOffset]         = varOffsets[vi]  (start)
        //   [maskOffset - (varCount+2)*SizeRowVarColOffset]   = eod
        int sz = _format.SizeRowVarColOffset;

        int varCountOffset = maskOffset - sz;
        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];

        if (vi >= storedVarCount) return null;

        int varStartOffset = maskOffset - (vi + 2) * sz;
        if (varStartOffset < 0) return null;

        int varStart = sz == 2
            ? ByteUtil.GetShort(rowBytes, varStartOffset)
            : rowBytes[varStartOffset];

        int varEnd;
        if (vi + 1 < storedVarCount)
        {
            // Next var col's start position = end of this col
            int nextOffset = maskOffset - (vi + 1 + 2) * sz;
            varEnd = sz == 2
                ? ByteUtil.GetShort(rowBytes, nextOffset)
                : rowBytes[nextOffset];
        }
        else
        {
            // Last var col: end = eod
            int eodOffset = maskOffset - (storedVarCount + 2) * sz;
            if (eodOffset < 0) return null;
            varEnd = sz == 2
                ? ByteUtil.GetShort(rowBytes, eodOffset)
                : rowBytes[eodOffset];
        }

        int varLen = varEnd - varStart;
        if (varLen < 0 || varStart < 0 || varEnd > maskOffset) return null;

        return col.DataType switch
        {
            DataType.Text   => ByteUtil.DecodeText(rowBytes, varStart, varLen, _format),
            DataType.Binary => rowBytes[varStart..varEnd],
            DataType.Memo   => DecodeMemoLvRef(rowBytes, varStart, varLen),
            DataType.Ole    => DecodeOleLvRef (rowBytes, varStart, varLen),
            _               => null
        };
    }

    /// <summary>
    /// Scans all long-value (Memo/OLE) columns in <paramref name="rowBytes"/> and yields
    /// the LVAL page/row coordinates for any that hold an OTHER_PAGE (0x40) LvRef.
    /// Used to locate LVAL chains that must be freed before a row is deleted or updated.
    /// </summary>
    public IEnumerable<(int lvalPage, int lvalRow)> GetOtherPageLvRefs(byte[] rowBytes)
    {
        if (rowBytes is null || rowBytes.Length < _format.SizeRowColumnCount)
            yield break;

        int colCount = _format.SizeRowColumnCount == 2
            ? ByteUtil.GetShort(rowBytes, 0)
            : rowBytes[0];

        int maskSize   = (colCount + 7) / 8;
        int maskOffset = rowBytes.Length - maskSize;
        if (maskOffset < _format.SizeRowColumnCount) yield break;

        int sz             = _format.SizeRowVarColOffset;
        int varCountOffset = maskOffset - sz;
        if (varCountOffset < 0) yield break;

        int storedVarCount = sz == 2
            ? ByteUtil.GetShort(rowBytes, varCountOffset)
            : rowBytes[varCountOffset];

        foreach (var col in _columns)
        {
            if (!col.DataType.IsLongValue()) continue;
            if (col.ColumnNumber >= colCount) continue;
            if (!_varIndex.TryGetValue(col.ColumnNumber, out int vi)) continue;
            if (vi >= storedVarCount) continue;

            // Check null bit.
            bool notNull = (rowBytes[maskOffset + col.ColumnNumber / 8]
                            & (1 << (col.ColumnNumber % 8))) != 0;
            if (!notNull) continue;

            // Locate the variable-length data for this column.
            int varStartOffset = maskOffset - (vi + 2) * sz;
            if (varStartOffset < 0) continue;

            int varStart = sz == 2
                ? ByteUtil.GetShort(rowBytes, varStartOffset)
                : rowBytes[varStartOffset];

            int varEnd;
            if (vi + 1 < storedVarCount)
            {
                int nextOffset = maskOffset - (vi + 1 + 2) * sz;
                if (nextOffset < 0) continue;
                varEnd = sz == 2
                    ? ByteUtil.GetShort(rowBytes, nextOffset)
                    : rowBytes[nextOffset];
            }
            else
            {
                int eodOffset = maskOffset - (storedVarCount + 2) * sz;
                if (eodOffset < 0) continue;
                varEnd = sz == 2
                    ? ByteUtil.GetShort(rowBytes, eodOffset)
                    : rowBytes[eodOffset];
            }

            int varLen = varEnd - varStart;
            if (varLen < 9 || varStart < 0 || varEnd > maskOffset) continue;

            // Check for OTHER_PAGE (0x40) marker at byte [4] of the LvRef.
            if (rowBytes[varStart + 4] != 0x40) continue;

            int lvalPage = rowBytes[varStart + 5]
                         | (rowBytes[varStart + 6] << 8)
                         | (rowBytes[varStart + 7] << 16);
            int lvalRow  = rowBytes[varStart + 8];

            yield return (lvalPage, lvalRow);
        }
    }

    // ── Fixed-type readers ────────────────────────────────────────────────────
    private static object? ReadFixed(byte[] row, int offset, Column col) =>
        col.DataType switch
        {
            DataType.Byte         => row[offset],
            DataType.Int          => ByteUtil.GetShort (row, offset),
            DataType.Long         => ByteUtil.GetInt   (row, offset),
            DataType.Float        => ByteUtil.GetFloat (row, offset),
            DataType.Double       => ByteUtil.GetDouble(row, offset),
            DataType.ShortDateTime=> DecodeShortDateTime(row, offset),
            DataType.Money        => Math.Round((decimal)ByteUtil.GetLong(row, offset) / 10000m, 4),
            DataType.Guid         => new Guid(row[offset..(offset + 16)]),
        DataType.Numeric      => DecodeNumeric(row, offset, col),
        _                     => null
    };

    // ── Long-value decoders ───────────────────────────────────────────────────

    /// <summary>
    /// Decodes a Memo LvRef from the variable-length area.
    /// Handles THIS_PAGE (0x80) inline refs and OTHER_PAGE (0x40) LVAL-page refs.
    /// Returns null if the ref type is unrecognised or an LVAL reader is unavailable.
    /// </summary>
    private string? DecodeMemoLvRef(byte[] row, int start, int len)
    {
        if (len < 5) return null;
        byte typeFlag = row[start + 4];

        if (typeFlag == 0x80)   // THIS_PAGE (inline)
        {
            int dataLen   = ByteUtil.GetInt(row, start);
            int actualLen = Math.Min(dataLen, len - 5);
            return ByteUtil.DecodeText(row, start + 5, actualLen, _format);
        }

        if (typeFlag == 0x40 && _lvalReader is not null)   // OTHER_PAGE
        {
            if (len < 9) return null;
            int dataLen  = ByteUtil.GetInt(row, start);
            int lvalPage = row[start + 5] | (row[start + 6] << 8) | (row[start + 7] << 16);
            int lvalRow  = row[start + 8];
            byte[] data  = _lvalReader.Read(lvalPage, lvalRow, dataLen);
            return ByteUtil.DecodeText(data, 0, data.Length, _format);
        }

        return null;   // OTHER_PAGE without LvalReader — cannot decode
    }

    private byte[]? DecodeOleLvRef(byte[] row, int start, int len)
    {
        if (len < 5) return null;
        byte typeFlag = row[start + 4];

        if (typeFlag == 0x80)   // THIS_PAGE (inline)
        {
            int dataLen   = ByteUtil.GetInt(row, start);
            int actualLen = Math.Min(dataLen, len - 5);
            var result    = new byte[actualLen];
            Array.Copy(row, start + 5, result, 0, actualLen);
            return result;
        }

        if (typeFlag == 0x40 && _lvalReader is not null)   // OTHER_PAGE
        {
            if (len < 9) return null;
            int dataLen  = ByteUtil.GetInt(row, start);
            int lvalPage = row[start + 5] | (row[start + 6] << 8) | (row[start + 7] << 16);
            int lvalRow  = row[start + 8];
            return _lvalReader.Read(lvalPage, lvalRow, dataLen);
        }

        return null;   // OTHER_PAGE without LvalReader — cannot decode
    }

    /// <summary>
    /// Decodes a Jet ShortDateTime (OLE Automation date = days since 1899-12-30).
    /// Out-of-range values (corruption, sentinels) become <see cref="DateTime.MinValue"/>
    /// rather than throw, matching Jackcess Java which returns the row but logs.
    /// Valid OADate range is roughly [-657434.0, 2958465.99] (years 100..9999).
    /// </summary>
    private static DateTime? DecodeShortDateTime(byte[] row, int offset)
    {
        double oa = ByteUtil.GetDouble(row, offset);
        if (double.IsNaN(oa) || double.IsInfinity(oa)) return null;
        if (oa < -657434.0 || oa > 2958465.99999999) return null;
        try   { return DateTime.FromOADate(oa); }
        catch { return null; }
    }

    /// <summary>
    /// Decodes a Jet Numeric column (17 bytes: 1 sign + 16 big-endian magnitude bytes).
    /// Returns <c>null</c> when the value's magnitude exceeds <see cref="decimal.MaxValue"/>
    /// after scaling — Jet Numeric can carry 28 digits which can overflow .NET decimal.
    /// </summary>
    private static decimal? DecodeNumeric(byte[] row, int offset, Column col)
    {
        bool isNegative = row[offset] == 0x80;
        // Read the 16 big-endian bytes and reverse to little-endian for BigInteger.
        // The 17th byte (index 16) is 0x00 so BigInteger treats the value as positive.
        var leBytes = new byte[17];
        for (int i = 0; i < 16; i++)
            leBytes[15 - i] = row[offset + 1 + i];
        var bigInt = new System.Numerics.BigInteger(leBytes);

        // Scale BigInteger BEFORE casting: divides by 10^scale so the result fits in decimal
        // when the original integer value didn't.
        System.Numerics.BigInteger divisor = System.Numerics.BigInteger.One;
        for (int s = 0; s < col.Scale; s++) divisor *= 10;
        var integerPart    = bigInt / divisor;
        var fractionalPart = bigInt - integerPart * divisor;

        // If the integer magnitude itself exceeds decimal.MaxValue, the value is too big to fit.
        if (integerPart > MaxDecimalAsBigInt || integerPart < MinDecimalAsBigInt) return null;

        decimal result = (decimal)integerPart;
        if (col.Scale > 0 && fractionalPart != 0)
            result += (decimal)fractionalPart / (decimal)divisor;
        return isNegative ? -result : result;
    }

    private static readonly System.Numerics.BigInteger MaxDecimalAsBigInt = new(decimal.MaxValue);
    private static readonly System.Numerics.BigInteger MinDecimalAsBigInt = new(decimal.MinValue);
}
