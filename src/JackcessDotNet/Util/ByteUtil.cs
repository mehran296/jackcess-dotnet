using System.Text;

namespace JackcessDotNet.Util;

/// <summary>
/// Low-level byte manipulation helpers for reading/writing Jet format data.
/// All Jet format values are little-endian.
/// </summary>
internal static class ByteUtil
{
    // ── Read helpers ─────────────────────────────────────────────────────────

    public static byte GetByte(byte[] buf, int offset) => buf[offset];

    public static short GetShort(byte[] buf, int offset)
        => (short)(buf[offset] | (buf[offset + 1] << 8));

    public static ushort GetUShort(byte[] buf, int offset)
        => (ushort)(buf[offset] | (buf[offset + 1] << 8));

    public static int GetInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16)
         | (buf[offset + 3] << 24);

    public static uint GetUInt(byte[] buf, int offset)
        => (uint)(buf[offset]
                | (buf[offset + 1] << 8)
                | (buf[offset + 2] << 16)
                | (buf[offset + 3] << 24));

    public static long GetLong(byte[] buf, int offset)
        => (long)(uint)GetInt(buf, offset)
         | ((long)(uint)GetInt(buf, offset + 4) << 32);

    public static double GetDouble(byte[] buf, int offset)
        => BitConverter.Int64BitsToDouble(GetLong(buf, offset));

    public static float GetFloat(byte[] buf, int offset)
        => BitConverter.Int32BitsToSingle(GetInt(buf, offset));

    // ── Write helpers ─────────────────────────────────────────────────────────

    public static void PutByte(byte[] buf, int offset, byte value)
        => buf[offset] = value;

    public static void PutShort(byte[] buf, int offset, short value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutUShort(byte[] buf, int offset, ushort value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    public static void PutInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
        buf[offset + 3] = (byte)((value >> 24) & 0xFF);
    }

    public static void PutUInt(byte[] buf, int offset, uint value)
        => PutInt(buf, offset, (int)value);

    public static void PutLong(byte[] buf, int offset, long value)
    {
        PutInt(buf, offset,     (int)(value & 0xFFFFFFFFL));
        PutInt(buf, offset + 4, (int)((value >> 32) & 0xFFFFFFFFL));
    }

    public static int Get3ByteInt(byte[] buf, int offset)
        => buf[offset]
         | (buf[offset + 1] << 8)
         | (buf[offset + 2] << 16);

    public static void Put3ByteInt(byte[] buf, int offset, int value)
    {
        buf[offset]     = (byte)(value & 0xFF);
        buf[offset + 1] = (byte)((value >> 8) & 0xFF);
        buf[offset + 2] = (byte)((value >> 16) & 0xFF);
    }

    public static void PutDouble(byte[] buf, int offset, double value)
        => PutLong(buf, offset, BitConverter.DoubleToInt64Bits(value));

    public static void PutFloat(byte[] buf, int offset, float value)
        => PutInt(buf, offset, BitConverter.SingleToInt32Bits(value));

    public static void PutBytes(byte[] buf, int offset, byte[] src)
        => Array.Copy(src, 0, buf, offset, src.Length);

    public static void PutBytes(byte[] buf, int offset, byte[] src, int srcOffset, int length)
        => Array.Copy(src, srcOffset, buf, offset, length);

    // ── String helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Encodes a string as compressed or uncompressed UTF-16LE for Jet Text columns.
    /// Jet uses a simple compression: if all code points fit in the Latin-1 range (0x00-0xFF)
    /// and the first byte is 0xFF (compression marker), then single bytes are used.
    /// We always write uncompressed UTF-16LE prefixed with the 0xFF 0xFE BOM marker approach,
    /// but Jet Text fields use the 0xFF marker for compressed mode.
    /// </summary>
    public static byte[] EncodeText(string value)
    {
        // Try compressed encoding when all chars fit in Latin-1.
        // Access uses a 2-byte marker (0xFF 0xFE) so the decoder can distinguish
        // compressed text from uncompressed UTF-16LE.
        bool canCompress = value.All(c => c <= 0xFF);
        if (canCompress)
        {
            var bytes = new byte[value.Length + 2];
            bytes[0] = 0xFF;
            bytes[1] = 0xFE;
            for (int i = 0; i < value.Length; i++)
                bytes[i + 2] = (byte)value[i];
            return bytes;
        }
        return Encoding.Unicode.GetBytes(value);
    }

    /// <summary>
    /// Jet4 text decoder. Real Access prefixes compressed text with the 2-byte
    /// marker 0xFF 0xFE and stores chars as single Latin-1 bytes. Uncompressed
    /// text is stored as UTF-16LE without a marker. The older 1-byte 0xFF prefix
    /// is also recognised so the format remains compatible with text written by
    /// earlier versions of this library.
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length)
    {
        if (length == 0) return string.Empty;
        if (length >= 2 && data[offset] == 0xFF && data[offset + 1] == 0xFE)
        {
            // Access-format compressed: 0xFF 0xFE + Latin-1 bytes.
            return Encoding.Latin1.GetString(data, offset + 2, length - 2);
        }
        if (data[offset] == 0xFF)
        {
            // Legacy single-byte marker (text we wrote ourselves before the fix).
            return Encoding.Latin1.GetString(data, offset + 1, length - 1);
        }
        return Encoding.Unicode.GetString(data, offset, length);
    }

    /// <summary>
    /// Format-aware text decoder.
    /// Jet3 text fields are stored directly as the database charset (cp1252) — no compression marker.
    /// Jet4 text fields use UTF-16LE with an optional 0xFF compression marker for Latin-1 strings.
    /// </summary>
    public static string DecodeText(byte[] data, int offset, int length, JetFormat format)
    {
        if (length == 0) return string.Empty;
        if (format.Version == JetVersion.Jet3)
            return format.TextEncoding.GetString(data, offset, length);
        return DecodeText(data, offset, length);
    }

    // ── Misc ──────────────────────────────────────────────────────────────────

    public static void Fill(byte[] buf, int offset, int count, byte value)
    {
        for (int i = 0; i < count; i++)
            buf[offset + i] = value;
    }

    public static void Clear(byte[] buf, int offset, int count)
        => Fill(buf, offset, count, 0x00);
}
