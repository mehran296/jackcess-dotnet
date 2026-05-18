using System.Security.Cryptography;
using System.Text;

namespace JackcessDotNet;

/// <summary>
/// RC4-based codec for password-protected Jet 4 (.mdb) databases. Implements
/// the "MSISAM" / Jet-4 encryption scheme as documented in MDBTools and reverse-
/// engineered notes:
///
///   • Page 0 (the database header) stores a 4-byte "encryption indicator"
///     at offset 0x3E and a 14-byte password verifier at offset 0x42.
///   • The user's password (UTF-16LE, truncated/padded to 14 bytes) is XOR'd
///     against the verifier to recover the 14-byte master key.
///   • For each page N, RC4 is keyed with [master ⊕ page-number] and the
///     resulting keystream is XOR'd against the page bytes.
///
/// <para>
/// <b>Verification status:</b> the RC4 primitive is verified against the RFC
/// test vectors in <c>CodecTests</c>. The page-key derivation is implemented
/// from documentation but has not been tested against a real encrypted Access
/// file (the project doesn't ship encrypted corpora). It compiles and behaves
/// deterministically; correctness on a real password-protected database is
/// best-effort until a test fixture lands.
/// </para>
/// </summary>
internal sealed class JetCryptCodecHandler : ICodecHandler
{
    private const int VerifierOffset       = 0x42;
    private const int VerifierLength       = 14;

    private readonly byte[] _masterKey;   // 14 bytes

    private JetCryptCodecHandler(byte[] masterKey) => _masterKey = masterKey;

    /// <summary>
    /// Derives the codec from the database header page and the user-supplied
    /// password. Returns null when the file isn't password-protected (verifier
    /// bytes all zero) — callers should fall back to <see cref="NullCodecHandler"/>.
    /// </summary>
    public static JetCryptCodecHandler? FromDbHeader(byte[] dbHeader, string? password)
    {
        if (dbHeader.Length < VerifierOffset + VerifierLength) return null;

        // Detect "no password": verifier bytes are all zero on unencrypted files.
        bool allZero = true;
        for (int i = 0; i < VerifierLength; i++)
            if (dbHeader[VerifierOffset + i] != 0) { allZero = false; break; }
        if (allZero) return null;

        byte[] passwordBytes = EncodePassword(password ?? string.Empty);
        var key = new byte[VerifierLength];
        for (int i = 0; i < VerifierLength; i++)
            key[i] = (byte)(dbHeader[VerifierOffset + i] ^ passwordBytes[i]);
        return new JetCryptCodecHandler(key);
    }

    public byte[] DecodePage(byte[] rawPage, int pageNumber)
        => Transform(rawPage, pageNumber);

    public byte[] EncodePage(byte[] page, int pageNumber)
        => Transform(page, pageNumber);

    /// <summary>
    /// Page 0 (the database header) is partly cleartext (the "Standard Jet DB"
    /// signature lives there); only pages numbered ≥ 1 are RC4-keyed.
    /// </summary>
    private byte[] Transform(byte[] page, int pageNumber)
    {
        if (pageNumber == 0) return page;

        byte[] perPageKey = BuildPerPageKey(_masterKey, pageNumber);
        byte[] keystream  = Rc4Keystream(perPageKey, page.Length);
        var result = new byte[page.Length];
        for (int i = 0; i < page.Length; i++)
            result[i] = (byte)(page[i] ^ keystream[i]);
        return result;
    }

    /// <summary>
    /// Combines the master key with the page number by XOR-ing the page number's
    /// little-endian bytes against the first four key bytes — the documented
    /// MSISAM page-key derivation.
    /// </summary>
    private static byte[] BuildPerPageKey(byte[] master, int pageNumber)
    {
        var k = (byte[])master.Clone();
        k[0] ^= (byte) (pageNumber        & 0xFF);
        k[1] ^= (byte)((pageNumber >>  8) & 0xFF);
        k[2] ^= (byte)((pageNumber >> 16) & 0xFF);
        k[3] ^= (byte)((pageNumber >> 24) & 0xFF);
        return k;
    }

    /// <summary>
    /// Pure RC4 stream cipher (RFC compliant). Keys may be any length 1..256;
    /// returns the first <paramref name="length"/> bytes of the keystream.
    /// </summary>
    internal static byte[] Rc4Keystream(byte[] key, int length)
    {
        // Key schedule.
        var s = new byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        int j2 = 0;
        for (int i = 0; i < 256; i++)
        {
            j2 = (j2 + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j2]) = (s[j2], s[i]);
        }

        // PRGA.
        var output = new byte[length];
        int x = 0, y = 0;
        for (int n = 0; n < length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = s[(s[x] + s[y]) & 0xFF];
        }
        return output;
    }

    /// <summary>
    /// Encodes the password to the 14-byte block the verifier XOR expects:
    /// UTF-16LE bytes, truncated or zero-padded to fit. Empty password → all zeros
    /// (which combined with a non-zero verifier produces the master key itself).
    /// </summary>
    private static byte[] EncodePassword(string password)
    {
        var buf = new byte[VerifierLength];
        if (string.IsNullOrEmpty(password)) return buf;
        byte[] u16 = Encoding.Unicode.GetBytes(password);
        Array.Copy(u16, 0, buf, 0, Math.Min(u16.Length, VerifierLength));
        return buf;
    }
}
