using System.Text;
using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Decodes the binary property-map blob stored in the "LvProp" column of
/// MSysObjects rows. Direct port of Jackcess Java's
/// <c>PropertyMaps.Handler.read</c>.
///
/// Wire format (Access 2000+):
///   [0..3]  signature "MR2\0" (Jet4+) or "KKD\0" (Jet3)
///   then a stream of chunks, each:
///     [4 bytes] chunk length (includes itself)
///     [2 bytes] block type:
///                 0x80 — property-name list
///                 0x00 — default-property-value list (table-level)
///                 0x01 — column-property-value list (one block per column)
///     [length-6 bytes] block data
///
///   Name-list block: sequence of (short length, name bytes) pairs.
///   Value-list block:
///     [4 bytes] map-name sub-block length (read a name inside if len > 6)
///     then per-property:
///         [2 bytes] value-entry length
///         [1 byte ] isDdl flag
///         [1 byte ] DataType code
///         [2 bytes] index into the name list
///         [2 bytes] value byte count
///         [N bytes] raw value bytes
/// </summary>
internal static class PropertyMapReader
{
    private static readonly byte[] SignatureJet4 = { (byte)'M', (byte)'R', (byte)'2', 0x00 };
    private static readonly byte[] SignatureJet3 = { (byte)'K', (byte)'K', (byte)'D', 0x00 };
    private const short BlockTypeNameList     = 0x0080;
    private const short BlockTypeDefaultValue = 0x0000;
    private const short BlockTypeColumnValue  = 0x0001;

    /// <summary>
    /// Decodes <paramref name="propBytes"/> into a <see cref="PropertyMaps"/>.
    /// Returns an empty <see cref="PropertyMaps"/> on null/empty input or when
    /// the signature is unrecognised.
    /// </summary>
    public static PropertyMaps Read(byte[]? propBytes, JetFormat format)
    {
        var maps = new PropertyMaps();
        if (propBytes is null || propBytes.Length < 4) return maps;

        int pos = 0;
        if (Matches(propBytes, 0, SignatureJet4)) pos = 4;
        else if (Matches(propBytes, 0, SignatureJet3)) pos = 4;
        else return maps;   // unknown signature — give up gracefully

        List<string>? propNames = null;
        while (pos + 6 <= propBytes.Length)
        {
            int   chunkLen   = ByteUtil.GetInt  (propBytes, pos);
            short blockType  = ByteUtil.GetShort(propBytes, pos + 4);
            int   blockStart = pos + 6;
            int   blockEnd   = pos + chunkLen;
            if (chunkLen <= 6 || blockEnd > propBytes.Length) break;

            if (blockType == BlockTypeNameList)
                propNames = ReadNameList(propBytes, blockStart, blockEnd, format);
            else if (propNames is not null)
                ReadValueBlock(propBytes, blockStart, blockEnd, blockType, propNames, format, maps);

            pos = blockEnd;
        }

        return maps;
    }

    private static List<string> ReadNameList(byte[] buf, int start, int end, JetFormat format)
    {
        var names = new List<string>();
        int pos = start;
        while (pos + 2 <= end)
        {
            int nameLen = ByteUtil.GetShort(buf, pos);
            pos += 2;
            if (nameLen < 0 || pos + nameLen > end) break;
            names.Add(DecodeName(buf, pos, nameLen, format));
            pos += nameLen;
        }
        return names;
    }

    private static void ReadValueBlock(
        byte[] buf, int start, int end, short blockType,
        List<string> propNames, JetFormat format, PropertyMaps maps)
    {
        int pos = start;

        // The block begins with a sub-block carrying the map name (or empty
        // for the default/table-level map). Layout: 4-byte sub-length, then if
        // sub-length > 6, a length-prefixed name.
        string mapName = string.Empty;
        if (pos + 4 <= end)
        {
            int subLen = ByteUtil.GetInt(buf, pos);
            int subEnd = pos + subLen;
            if (subLen > 6 && subEnd <= end)
            {
                int nameLen = ByteUtil.GetShort(buf, pos + 4);
                if (nameLen >= 0 && pos + 6 + nameLen <= end)
                    mapName = DecodeName(buf, pos + 6, nameLen, format);
            }
            pos = subEnd;
        }

        var map = new PropertyMap(mapName, (byte)blockType);

        while (pos + 8 <= end)
        {
            int  valLen   = ByteUtil.GetShort(buf, pos);
            int  valEnd   = pos + valLen;
            if (valLen < 8 || valEnd > end) break;
            bool isDdl    = buf[pos + 2] != 0;
            byte dtByte   = buf[pos + 3];
            int  nameIdx  = ByteUtil.GetShort(buf, pos + 4);
            int  dataSize = ByteUtil.GetShort(buf, pos + 6);
            int  dataStart = pos + 8;
            if (dataSize < 0 || dataStart + dataSize > end) break;

            if (nameIdx >= 0 && nameIdx < propNames.Count)
            {
                string propName = propNames[nameIdx];
                var dataType = (DataType)dtByte;
                object? value = DecodeValue(buf, dataStart, dataSize, dataType, format);
                map.Add(new PropertyMap.Property(propName, dataType, value, isDdl));
            }

            pos = valEnd;
        }

        maps.Add(map);
    }

    private static string DecodeName(byte[] buf, int pos, int len, JetFormat format)
    {
        // Property names use the database charset (cp1252 for Jet3, UTF-16LE for Jet4+).
        return format.Version == JetVersion.Jet3
            ? format.TextEncoding.GetString(buf, pos, len)
            : Encoding.Unicode.GetString(buf, pos, len);
    }

    private static object? DecodeValue(byte[] buf, int pos, int len, DataType dt, JetFormat format)
    {
        if (len == 0) return null;
        return dt switch
        {
            DataType.Boolean       => buf[pos] != 0,
            DataType.Byte          => buf[pos],
            DataType.Int           => ByteUtil.GetShort (buf, pos),
            DataType.Long          => ByteUtil.GetInt   (buf, pos),
            DataType.Money         => Math.Round((decimal)ByteUtil.GetLong(buf, pos) / 10000m, 4),
            DataType.Float         => ByteUtil.GetFloat (buf, pos),
            DataType.Double        => ByteUtil.GetDouble(buf, pos),
            DataType.ShortDateTime => TryFromOADate(ByteUtil.GetDouble(buf, pos)),
            DataType.Guid          => new Guid(buf[pos..(pos + Math.Min(16, len))]),
            DataType.Text          => format.Version == JetVersion.Jet3
                                          ? format.TextEncoding.GetString(buf, pos, len)
                                          : Encoding.Unicode.GetString(buf, pos, len),
            DataType.Memo          => format.Version == JetVersion.Jet3
                                          ? format.TextEncoding.GetString(buf, pos, len)
                                          : Encoding.Unicode.GetString(buf, pos, len),
            DataType.Binary        => buf[pos..(pos + len)],
            DataType.Ole           => buf[pos..(pos + len)],
            _                      => null,
        };
    }

    private static DateTime? TryFromOADate(double oa)
    {
        if (double.IsNaN(oa) || double.IsInfinity(oa)) return null;
        if (oa < -657434.0 || oa > 2958465.99999999) return null;
        try   { return DateTime.FromOADate(oa); }
        catch { return null; }
    }

    private static bool Matches(byte[] buf, int offset, byte[] expected)
    {
        if (offset + expected.Length > buf.Length) return false;
        for (int i = 0; i < expected.Length; i++)
            if (buf[offset + i] != expected[i]) return false;
        return true;
    }
}
