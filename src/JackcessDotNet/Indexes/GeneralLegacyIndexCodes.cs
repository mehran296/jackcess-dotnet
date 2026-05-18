using System.Globalization;
using System.Reflection;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// Encodes a text value into the byte form Access uses for index entries,
/// using the "general legacy" sort order (Access 2000-2007 default).
///
/// Direct port of <c>io.github.spannm.jackcess.impl.GeneralLegacyIndexCodes</c>.
/// Per-codepoint code tables for the basic Latin-1 block (U+0000..U+00FF) are
/// loaded from the embedded <c>index_codes_genleg.txt</c> resource at startup;
/// characters &gt; U+00FF are treated as IGNORED (a known limitation — those
/// require the EXT codes file which is not yet ported).
///
/// Output byte stream layout for a text value:
///   [inline bytes per char]
///   END_TEXT (0x01)
///   [extra/international bytes section, if any]
///   [END_TEXT × 2 if crazy or unprintable]
///   [crazy codes packed into 6-bit groups, if any]
///   [CRAZY_CODES_UNPRINT_SUFFIX if both]
///   [END_TEXT before unprintable section, if any]
///   [unprintable codes]
///   [END_EXTRA_TEXT then byte-flip of the whole text if DESC]
///   END_EXTRA_TEXT (0x00)
/// </summary>
internal static class GeneralLegacyIndexCodes
{
    private const byte EndText                          = 0x01;
    private const byte EndExtraText                     = 0x00;
    private const byte InternationalExtraPlaceholder    = 0x02;
    private const int  UnprintableCountStart            = 7;
    private const int  UnprintableCountMultiplier       = 4;
    private const int  UnprintableOffsetFlags           = 0x8000;
    private const byte UnprintableMidfix                = 0x06;
    private const byte CrazyCodeStart                   = 0x80;
    private const byte CrazyCode1                       = 0x02;
    private const byte CrazyCode2                       = 0x03;
    private static readonly byte[] CrazyCodesSuffix     = { 0xFF, 0x02, 0x80, 0xFF, 0x80 };
    private const byte CrazyCodesUnprintSuffix          = 0xFF;

    /// <summary>Max text length used as an index key (matches Jet TEXT_FIELD_MAX_LENGTH/UNIT_SIZE).</summary>
    private const int MaxTextIndexCharLength = 255;

    private static readonly CharHandler[] _basicCodes = LoadCodes("index_codes_genleg.txt", 0x100);

    /// <summary>
    /// Code table for U+0100..U+FFFF, excluding the surrogate range U+D800..U+DFFF.
    /// Lazily loaded on first access via <see cref="ExtCodes.Values"/>.
    /// </summary>
    private static class ExtCodes
    {
        // EXT file covers U+0100..U+FFFF excluding U+D800..U+DFFF surrogates
        // = (0xFFFF - 0x0100 + 1) - 2048 = 65280 - 2048 = 63232 entries.
        public static readonly CharHandler[] Values = LoadCodes("index_codes_ext_genleg.txt", 63232);
    }

    private const int FirstExtChar         = 0x100;
    private const int FirstHighSurrogate   = 0xD800;
    private const int LastLowSurrogate     = 0xDFFF;
    private const int SurrogateRangeCount  = 0xE000 - 0xD800;   // = 2048

    /// <summary>
    /// Encodes <paramref name="value"/> into Jet index-key byte form using the
    /// general legacy sort order.
    /// </summary>
    public static byte[] EncodeText(string value, bool isAscending)
    {
        string str = ToIndexCharSequence(value);

        var bout = new MemoryStream();
        int prevLength = 0;

        ExtraCodesStream? extraCodes      = null;
        MemoryStream?     unprintableCodes = null;
        MemoryStream?     crazyCodes       = null;
        int charOffset = 0;

        foreach (char c in str)
        {
            var handler = GetCharHandler(c);
            int curCharOffset = charOffset;

            byte[]? inline = handler.GetInlineBytes();
            if (inline is not null)
            {
                bout.Write(inline, 0, inline.Length);
                charOffset++;
            }

            if (handler.Kind == HandlerKind.Simple) continue;

            byte[]? extra = handler.GetExtraBytes();
            byte modifier = handler.GetExtraByteModifier();
            if (extra is not null || modifier != 0)
            {
                extraCodes ??= new ExtraCodesStream();
                WriteExtraCodes(curCharOffset, extra, modifier, extraCodes);
            }

            byte[]? unprint = handler.GetUnprintableBytes();
            if (unprint is not null)
            {
                unprintableCodes ??= new MemoryStream();
                WriteUnprintableCodes(curCharOffset, unprint, unprintableCodes, extraCodes);
            }

            byte crazy = handler.GetCrazyFlag();
            if (crazy != 0)
            {
                crazyCodes ??= new MemoryStream();
                crazyCodes.WriteByte(crazy);
            }
        }

        bout.WriteByte(EndText);

        bool hasExtra  = TrimExtraCodes(extraCodes, 0, InternationalExtraPlaceholder);
        bool hasUnprint = unprintableCodes is not null;
        bool hasCrazy   = crazyCodes is not null;

        if (hasExtra || hasUnprint || hasCrazy)
        {
            if (hasExtra)
                extraCodes!.WriteTo(bout);

            if (hasCrazy || hasUnprint)
            {
                bout.WriteByte(EndText);
                bout.WriteByte(EndText);

                if (hasCrazy)
                {
                    WriteCrazyCodes(crazyCodes!, bout);
                    if (hasUnprint)
                        bout.WriteByte(CrazyCodesUnprintSuffix);
                }

                if (hasUnprint)
                {
                    bout.WriteByte(EndText);
                    unprintableCodes!.WriteTo(bout);
                }
            }
        }

        // Handle DESC: flip everything since prevLength, append an extra END_EXTRA_TEXT before flip.
        if (!isAscending)
        {
            bout.WriteByte(EndExtraText);
            byte[] all = bout.ToArray();
            for (int i = prevLength; i < all.Length; i++)
                all[i] = (byte)~all[i];
            bout = new MemoryStream();
            bout.Write(all, 0, all.Length);
        }

        bout.WriteByte(EndExtraText);
        return bout.ToArray();
    }

    private static string ToIndexCharSequence(object value)
    {
        string str = value?.ToString() ?? string.Empty;
        if (str.Length > MaxTextIndexCharLength)
            str = str.Substring(0, MaxTextIndexCharLength);
        // Trailing spaces are ignored for text index entries.
        int len = str.Length;
        while (len > 0 && str[len - 1] == ' ') len--;
        return len == str.Length ? str : str.Substring(0, len);
    }

    // ── Char handler lookup ───────────────────────────────────────────────────

    private static CharHandler GetCharHandler(char c)
    {
        if (c <= 0xFF) return _basicCodes[c];

        // The EXT codes file covers U+0100..U+FFFF excluding the surrogate range
        // U+D800..U+DFFF (Jackcess writes these as IGNORED via dedicated handlers).
        // Convert the codepoint into an index into the EXT array, skipping over the
        // 2048-codepoint surrogate hole that isn't represented in the file.
        if (c >= FirstHighSurrogate && c <= LastLowSurrogate)
            return IgnoredHandler.Instance;

        int idx = c - FirstExtChar;
        if (c > LastLowSurrogate) idx -= SurrogateRangeCount;
        if (idx < 0 || idx >= ExtCodes.Values.Length) return IgnoredHandler.Instance;
        return ExtCodes.Values[idx];
    }

    // ── Writers for the side streams ──────────────────────────────────────────

    private static void WriteExtraCodes(
        int charOffset, byte[]? bytes, byte extraCodeModifier, ExtraCodesStream extraCodes)
    {
        int numChars = extraCodes.NumChars;
        if (numChars < charOffset)
        {
            int fillChars = charOffset - numChars;
            for (int i = 0; i < fillChars; i++) extraCodes.WriteByte(InternationalExtraPlaceholder);
            extraCodes.NumChars += fillChars;
        }

        if (bytes is not null)
        {
            extraCodes.Write(bytes, 0, bytes.Length);
            extraCodes.NumChars++;
        }
        else
        {
            int lastIdx = (int)extraCodes.Length - 1;
            if (lastIdx >= 0)
            {
                byte lastByte = extraCodes.GetByteAt(lastIdx);
                lastByte += extraCodeModifier;
                extraCodes.SetByteAt(lastIdx, lastByte);
            }
            else
            {
                extraCodes.WriteByte(extraCodeModifier);
                extraCodes.UnprintablePrefixLen = 1;
            }
        }
    }

    private static bool TrimExtraCodes(ExtraCodesStream? extraCodes, byte minTrim, byte maxTrim)
    {
        if (extraCodes is null) return false;
        long len = extraCodes.Length;
        while (len > 0)
        {
            byte b = extraCodes.GetByteAt((int)len - 1);
            if (b < minTrim || b > maxTrim) break;
            len--;
        }
        extraCodes.SetLength(len);
        return len > 0;
    }

    private static void WriteUnprintableCodes(
        int charOffset, byte[] bytes, MemoryStream unprintableCodes, ExtraCodesStream? extraCodes)
    {
        int unprintCharOffset = charOffset;
        if (extraCodes is not null)
        {
            unprintCharOffset = (int)extraCodes.Length
                              + charOffset
                              - extraCodes.NumChars
                              - extraCodes.UnprintablePrefixLen;
        }

        int offset = UnprintableCountStart
                   + UnprintableCountMultiplier * unprintCharOffset
                   | UnprintableOffsetFlags;

        unprintableCodes.WriteByte((byte)((offset >> 8) & 0xFF));
        unprintableCodes.WriteByte((byte)(offset & 0xFF));
        unprintableCodes.WriteByte(UnprintableMidfix);
        unprintableCodes.Write(bytes, 0, bytes.Length);
    }

    private static void WriteCrazyCodes(MemoryStream crazyCodes, MemoryStream bout)
    {
        // Trim trailing CRAZY_CODE_2 bytes.
        byte[] codes = crazyCodes.ToArray();
        int len = codes.Length;
        while (len > 0 && codes[len - 1] == CrazyCode2) len--;

        if (len > 0)
        {
            byte curByte = CrazyCodeStart;
            int idx = 0;
            for (int i = 0; i < len; i++)
            {
                byte nextByte = codes[i];
                nextByte <<= (2 - idx) * 2;
                curByte |= nextByte;
                idx++;
                if (idx == 3)
                {
                    bout.WriteByte(curByte);
                    curByte = CrazyCodeStart;
                    idx = 0;
                }
            }
            if (idx > 0) bout.WriteByte(curByte);
        }

        bout.Write(CrazyCodesSuffix, 0, CrazyCodesSuffix.Length);
    }

    // ── Char handler hierarchy ────────────────────────────────────────────────

    private enum HandlerKind { Simple, Ignored, Unprintable, International, UnprintableExt, InternationalExt, Significant }

    private abstract class CharHandler
    {
        public abstract HandlerKind Kind { get; }
        public virtual byte[]? GetInlineBytes()      => null;
        public virtual byte[]? GetExtraBytes()       => null;
        public virtual byte[]? GetUnprintableBytes() => null;
        public virtual byte    GetExtraByteModifier()=> 0;
        public virtual byte    GetCrazyFlag()        => 0;
    }

    private sealed class IgnoredHandler : CharHandler
    {
        public static readonly IgnoredHandler Instance = new();
        public override HandlerKind Kind => HandlerKind.Ignored;
    }

    private sealed class SimpleHandler : CharHandler
    {
        private readonly byte[] _bytes;
        public SimpleHandler(byte[] bytes) => _bytes = bytes;
        public override HandlerKind Kind => HandlerKind.Simple;
        public override byte[] GetInlineBytes() => _bytes;
    }

    private sealed class InternationalHandler : CharHandler
    {
        private readonly byte[] _bytes;
        private readonly byte[] _extraBytes;
        public InternationalHandler(byte[] bytes, byte[] extra) { _bytes = bytes; _extraBytes = extra; }
        public override HandlerKind Kind => HandlerKind.International;
        public override byte[] GetInlineBytes() => _bytes;
        public override byte[] GetExtraBytes()  => _extraBytes;
    }

    private sealed class UnprintableHandler : CharHandler
    {
        private readonly byte[] _unprintBytes;
        public UnprintableHandler(byte[] unprintBytes) => _unprintBytes = unprintBytes;
        public override HandlerKind Kind => HandlerKind.Unprintable;
        public override byte[] GetUnprintableBytes() => _unprintBytes;
    }

    private sealed class UnprintableExtHandler : CharHandler
    {
        private readonly byte _modifier;
        public UnprintableExtHandler(byte modifier) => _modifier = modifier;
        public override HandlerKind Kind => HandlerKind.UnprintableExt;
        public override byte GetExtraByteModifier() => _modifier;
    }

    private sealed class InternationalExtHandler : CharHandler
    {
        private readonly byte[] _bytes;
        private readonly byte[] _extraBytes;
        private readonly byte _crazyFlag;
        public InternationalExtHandler(byte[] bytes, byte[] extra, byte crazyFlag)
        { _bytes = bytes; _extraBytes = extra; _crazyFlag = crazyFlag; }
        public override HandlerKind Kind => HandlerKind.InternationalExt;
        public override byte[] GetInlineBytes() => _bytes;
        public override byte[] GetExtraBytes()  => _extraBytes;
        public override byte   GetCrazyFlag()   => _crazyFlag;
    }

    private sealed class SignificantHandler : CharHandler
    {
        private readonly byte[] _bytes;
        public SignificantHandler(byte[] bytes) => _bytes = bytes;
        public override HandlerKind Kind => HandlerKind.Significant;
        public override byte[] GetInlineBytes() => _bytes;
    }

    /// <summary>
    /// Extension of MemoryStream that tracks two extra counters used by the
    /// extra-codes accumulator (number of chars written, and the length of any
    /// "unprintable" code prefix that doesn't count as a real char position).
    /// </summary>
    private sealed class ExtraCodesStream : MemoryStream
    {
        public int NumChars { get; set; }
        public int UnprintablePrefixLen { get; set; }
        public byte GetByteAt(int idx)            => GetBuffer()[idx];
        public void SetByteAt(int idx, byte b)    => GetBuffer()[idx] = b;
    }

    // ── Codes file loader ─────────────────────────────────────────────────────

    private static CharHandler[] LoadCodes(string resourceName, int numCodes)
    {
        var asm = typeof(GeneralLegacyIndexCodes).Assembly;
        string resPath = $"JackcessDotNet.Resources.{resourceName}";
        using var stream = asm.GetManifestResourceStream(resPath)
            ?? throw new InvalidOperationException($"Missing embedded resource '{resPath}'.");
        using var reader = new StreamReader(stream, Encoding.ASCII);

        var values = new CharHandler[numCodes];
        for (int i = 0; i < numCodes; i++)
        {
            string? line = reader.ReadLine();
            if (line is null)
            {
                values[i] = IgnoredHandler.Instance;
                continue;
            }
            values[i] = ParseCodeLine(line);
        }
        return values;
    }

    private static CharHandler ParseCodeLine(string line)
    {
        if (string.IsNullOrEmpty(line)) return IgnoredHandler.Instance;
        char prefix = line[0];
        string suffix = line.Length > 1 ? line.Substring(1) : string.Empty;

        try
        {
            return prefix switch
            {
                'X' => IgnoredHandler.Instance,
                'S' => new SimpleHandler        (HexToBytes(suffix)),
                'U' => new UnprintableHandler   (HexToBytes(suffix)),
                'P' => ParseUnprintableExt      (suffix),
                'I' => ParseInternational       (suffix),
                'Z' => ParseInternationalExt    (suffix),
                'G' => new SignificantHandler   (HexToBytes(suffix)),
                'Q' => IgnoredHandler.Instance,
                _   => IgnoredHandler.Instance,
            };
        }
        catch
        {
            // Malformed lines (typically single-hex-digit entries like "PC" that
            // strict 2-char-pair parsing rejects) are treated as IGNORED rather
            // than blowing up at load time. Matches Jackcess Java's effective
            // behaviour, since its codes are loaded lazily per-character.
            return IgnoredHandler.Instance;
        }
    }

    private static CharHandler ParseUnprintableExt(string suffix)
    {
        byte[] bytes = HexToBytes(suffix);
        if (bytes.Length < 1) return IgnoredHandler.Instance;
        return new UnprintableExtHandler(bytes[0]);
    }

    private static CharHandler ParseInternational(string suffix)
    {
        // "I" type: "inline_hex,extra_hex"
        var parts = suffix.Split(',');
        var inline = HexToBytes(parts[0]);
        var extra  = parts.Length > 1 ? HexToBytes(parts[1]) : Array.Empty<byte>();
        return new InternationalHandler(inline, extra);
    }

    private static CharHandler ParseInternationalExt(string suffix)
    {
        // "Z" type: "inline_hex,extra_hex,1_or_2"
        var parts = suffix.Split(',');
        var inline = HexToBytes(parts[0]);
        var extra  = parts.Length > 1 ? HexToBytes(parts[1]) : Array.Empty<byte>();
        byte crazy = parts.Length > 2 && parts[2] == "1" ? CrazyCode1 : CrazyCode2;
        return new InternationalExtHandler(inline, extra, crazy);
    }

    /// <summary>
    /// Mirrors Jackcess Java's hex-byte parser: takes <c>hex.Length / 2</c> bytes from
    /// the start; odd trailing chars are silently dropped. The codes file relies on
    /// that convention — entries like "U3" yield an empty byte array, and "S803"
    /// yields a single 0x80 byte (the "3" is discarded).
    /// </summary>
    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        int numBytes = hex.Length / 2;
        var result = new byte[numBytes];
        for (int i = 0; i < numBytes; i++)
            result[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return result;
    }
}
