namespace JackcessDotNet;

public sealed class RowEncoder
{
    private readonly JetFormat _format;
    private readonly IReadOnlyList<Column> _columns;
    private readonly Dictionary<Column, short> _fixedOffsets = new();
    private readonly Dictionary<Column, short> _varIndexes = new();
    private readonly int _varColumnCount;
    private readonly IReadOnlyDictionary<string, LvalWriter>? _lvalWriters;

    public RowEncoder(JetFormat format, IReadOnlyList<Column> columns)
        : this(format, columns, null) { }

    internal RowEncoder(JetFormat format, IReadOnlyList<Column> columns,
                        IReadOnlyDictionary<string, LvalWriter>? lvalWriters)
    {
        _lvalWriters = lvalWriters;
        _format = format ?? throw new ArgumentNullException(nameof(format));
        _columns = columns ?? throw new ArgumentNullException(nameof(columns));

        // Build layout in column-number order (ascending ColumnNumber), mirroring RowDecoder.
        // This means fixed-length data is laid out in column-number order, and var indices are
        // assigned in column-number order — both matching the Jackcess / Access on-disk format.
        short fixedOffset = 0;
        short varIndex = 0;
        foreach (var col in _columns.OrderBy(c => c.ColumnNumber))
        {
            if (col.DataType.IsVariableLength())
            {
                _varIndexes[col] = varIndex++;
            }
            else
            {
                _fixedOffsets[col] = fixedOffset;
                fixedOffset += (short)col.Length;
            }
        }
        _varColumnCount = varIndex;
    }

    public byte[] Encode(Row row)
    {
        if (row is null)
            throw new ArgumentNullException(nameof(row));

        int maxRowSize = _format.MaxRowSize;
        var buffer = new byte[maxRowSize];
        int pos = 0;

        // Column count
        if (_format.SizeRowColumnCount == 2)
        {
            Util.ByteUtil.PutShort(buffer, pos, (short)_columns.Count);
            pos += 2;
        }
        else if (_format.SizeRowColumnCount == 1)
        {
            if (_columns.Count > 255)
                throw new InvalidOperationException("Jet3 rows with >255 columns are not supported.");
            buffer[pos++] = (byte)_columns.Count;
        }
        else
        {
            throw new InvalidOperationException("Unsupported row column count size.");
        }

        var nullMask = new NullMask(_columns.Count);

        // Fixed data area — written in column-number order to match RowDecoder
        int fixedDataStart = pos;
        int fixedDataEnd = fixedDataStart;
        foreach (var col in _columns.OrderBy(c => c.ColumnNumber))
        {
            if (col.DataType.IsVariableLength())
                continue;

            object? value = row.TryGetValue(col.Name, out var v) ? v : null;

            if (col.DataType == DataType.Boolean)
            {
                if (value is bool b && b)
                    nullMask.MarkNotNull(col.ColumnNumber);
                continue;
            }

            if (value != null)
            {
                nullMask.MarkNotNull(col.ColumnNumber);
                int writePos = fixedDataStart + _fixedOffsets[col];
                WriteFixedValue(buffer, writePos, col, value);
            }

            int endPos = fixedDataStart + _fixedOffsets[col] + col.Length;
            if (endPos > fixedDataEnd)
                fixedDataEnd = endPos;
        }

        pos = fixedDataEnd;

        if (_varColumnCount > 0)
        {
            int trailerSize = nullMask.ByteSize + 4 + _varColumnCount * _format.SizeRowVarColOffset;
            int remaining = maxRowSize - pos - trailerSize;
            if (remaining < 0)
                throw new InvalidOperationException("Row is too large for format.");

            var varOffsets = new short[_varColumnCount];
            int varOffsetIndex = 0;

            // Variable-length data — also in column-number order to match var index assignment
            foreach (var col in _columns.OrderBy(c => c.ColumnNumber))
            {
                if (!col.DataType.IsVariableLength())
                    continue;

                short offset = (short)pos;
                object? value = row.TryGetValue(col.Name, out var v) ? v : null;
                if (value != null)
                {
                    nullMask.MarkNotNull(col.ColumnNumber);
                    byte[] varData = WriteVariableValue(col, value);

                    if (_format.SizeRowVarColOffset == 1 && (offset > 255 || varData.Length > 255))
                        throw new NotSupportedException("Jet3 rows with var offsets >255 are not supported yet.");

                    if (varData.Length > remaining)
                        throw new InvalidOperationException("Row is too large for format.");

                    Array.Copy(varData, 0, buffer, pos, varData.Length);
                    pos += varData.Length;
                    remaining -= varData.Length;
                }

                while (varOffsetIndex <= _varIndexes[col])
                {
                    varOffsets[varOffsetIndex++] = offset;
                }
            }

            while (varOffsetIndex < varOffsets.Length)
            {
                varOffsets[varOffsetIndex++] = (short)pos;
            }

            int eod = pos;
            Util.ByteUtil.PutShort(buffer, pos, (short)eod);
            pos += 2;

            for (int i = _varColumnCount - 1; i >= 0; i--)
            {
                if (_format.SizeRowVarColOffset == 2)
                {
                    Util.ByteUtil.PutShort(buffer, pos, varOffsets[i]);
                    pos += 2;
                }
                else if (_format.SizeRowVarColOffset == 1)
                {
                    buffer[pos++] = (byte)varOffsets[i];
                }
            }

            Util.ByteUtil.PutShort(buffer, pos, (short)_varColumnCount);
            pos += 2;
        }

        nullMask.WriteTo(buffer, pos);
        pos += nullMask.ByteSize;

        var result = new byte[pos];
        Array.Copy(buffer, 0, result, 0, pos);
        return result;
    }

    private void WriteFixedValue(byte[] buffer, int offset, Column col, object value)
    {
        switch (col.DataType)
        {
            case DataType.Byte:
                buffer[offset] = Convert.ToByte(value);
                return;
            case DataType.Int:
                Util.ByteUtil.PutShort(buffer, offset, Convert.ToInt16(value));
                return;
            case DataType.Long:
                Util.ByteUtil.PutInt(buffer, offset, Convert.ToInt32(value));
                return;
            case DataType.Float:
                Util.ByteUtil.PutFloat(buffer, offset, Convert.ToSingle(value));
                return;
            case DataType.Double:
                Util.ByteUtil.PutDouble(buffer, offset, Convert.ToDouble(value));
                return;
            case DataType.ShortDateTime:
                Util.ByteUtil.PutDouble(buffer, offset, ((DateTime)value).ToOADate());
                return;
            case DataType.Money:
                decimal dec = Convert.ToDecimal(value);
                long scaled = (long)Math.Round(dec * 10000m, 0, MidpointRounding.AwayFromZero);
                Util.ByteUtil.PutLong(buffer, offset, scaled);
                return;
            case DataType.Guid:
                var guidBytes = ((Guid)value).ToByteArray();
                Util.ByteUtil.PutBytes(buffer, offset, guidBytes);
                return;
            case DataType.Numeric:
            {
                decimal numVal = Convert.ToDecimal(value);
                buffer[offset] = numVal < 0m ? (byte)0x80 : (byte)0x00;
                decimal absVal = Math.Abs(numVal);
                decimal scaleFactor = 1m;
                for (int s = 0; s < col.Scale; s++) scaleFactor *= 10m;
                decimal numScaled = Math.Round(absVal * scaleFactor, 0, MidpointRounding.AwayFromZero);
                var bigInt = new System.Numerics.BigInteger(numScaled);
                byte[] leBytes = bigInt.ToByteArray();   // little-endian, may include a sign byte
                Array.Clear(buffer, offset + 1, 16);
                int copyLen = Math.Min(leBytes.Length, 16);
                for (int i = 0; i < copyLen; i++)
                    buffer[offset + 1 + (15 - i)] = leBytes[i];  // reverse to big-endian
                return;
            }
            default:
                throw new NotSupportedException($"Unsupported fixed data type {col.DataType}.");
        }
    }

    // ── Long-value helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Builds a THIS_PAGE (0x80) inline LvRef: [4-byte data-length LE][0x80][data bytes].
    /// The entire LvRef is stored in the row's variable-length area.
    /// </summary>
    private static byte[] BuildInlineLvRef(byte[] data)
    {
        var lvRef = new byte[5 + data.Length];
        Util.ByteUtil.PutInt(lvRef, 0, data.Length);
        lvRef[4] = 0x80;                                     // THIS_PAGE marker
        Array.Copy(data, 0, lvRef, 5, data.Length);
        return lvRef;
    }

    private byte[] WriteVariableValue(Column col, object value)
    {
        switch (col.DataType)
        {
            case DataType.Text:
                if (_format.Version == JetVersion.Jet3)
                    return _format.TextEncoding.GetBytes(Convert.ToString(value) ?? string.Empty);
                return Util.ByteUtil.EncodeText(Convert.ToString(value) ?? string.Empty);
            case DataType.Binary:
                return (byte[])value;
            case DataType.Memo:
            {
                string text = Convert.ToString(value) ?? string.Empty;
                byte[] textBytes = _format.Version == JetVersion.Jet3
                    ? _format.TextEncoding.GetBytes(text)
                    : Util.ByteUtil.EncodeText(text);
                if (textBytes.Length > 0
                    && _lvalWriters is not null
                    && _lvalWriters.TryGetValue(col.Name, out var memoWriter))
                    return memoWriter.Write(textBytes);
                return BuildInlineLvRef(textBytes);
            }
            case DataType.Ole:
            {
                if (value is not byte[] oleBytes)
                    throw new InvalidOperationException("OLE column value must be a byte array.");
                if (oleBytes.Length > 0
                    && _lvalWriters is not null
                    && _lvalWriters.TryGetValue(col.Name, out var oleWriter))
                    return oleWriter.Write(oleBytes);
                return BuildInlineLvRef(oleBytes);
            }
            default:
                throw new NotSupportedException($"Unsupported variable data type {col.DataType}.");
        }
    }
}
