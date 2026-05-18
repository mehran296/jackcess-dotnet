using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace JackcessDotNet;

/// <summary>
/// Office Crypto codec for password-protected <c>.accdb</c> databases. Covers
/// two of the schemes Access can produce:
///
/// <list type="bullet">
///   <item>
///     <b>Agile Encryption</b> (Office 2010+, vMajor=4, vMinor=4) —
///     MS-OFFCRYPTO §2.3.4.10–13. PBKDF2-style key derivation with configurable
///     hash, AES in CBC mode for page crypto.
///   </item>
///   <item>
///     <b>ECMA Standard Encryption</b> (Office 2007, vMajor∈{2,3,4}, vMinor=2)
///     — MS-OFFCRYPTO §2.3.4.5–9. SHA-1 over 50 000 iterations, AES in ECB mode
///     for page crypto, key size declared in the EncryptionHeader struct.
///   </item>
/// </list>
///
/// Both variants are read-only — encryption (write) is out of scope; encrypted
/// files open and decode pages, attempts to write throw <see cref="NotSupportedException"/>.
/// </summary>
public sealed class OfficeCryptCodecHandler : ICodecHandler
{
    // Header / encryption-info offsets shared across all schemes.
    private const int OffsetEncodingKey    = 0x3E;
    private const int EncodingKeyLength    = 4;
    private const int CryptStructureOffset = 0x299;

    // Bytes 38..42 of BASE_HEADER_MASK (Jackcess's per-byte XOR mask for page 0,
    // starting at offset OFFSET_MASKED_HEADER = 0x18). At rest the encoding-key
    // slot (offset 0x3E = mask index 38) is XOR'd with these 4 bytes; we have
    // to undo the mask before using the value as crypto input.
    private static readonly byte[] HeaderMaskAtEncodingKey = { 0xFB, 0x8A, 0xBC, 0x4E };

    // Flags inside the EncryptionHeader (MS-OFFCRYPTO 2.3.2).
    private const int FCryptoApiFlag = 0x04;
    private const int FExternalFlag  = 0x10;
    private const int FAesFlag       = 0x20;

    // 4-byte ALG_ID codes from EncryptionHeader (CryptoAPI ALG_ID).
    private const int AlgIdAes128 = 0x660E;
    private const int AlgIdAes192 = 0x660F;
    private const int AlgIdAes256 = 0x6610;
    private const int AlgIdRc4    = 0x6801;

    // 4-byte ALG_ID for hash algorithm — SHA-1 is the only one Standard uses.
    private const int HashAlgIdSha1 = 0x8004;

    private readonly byte[] _encodingKey;
    private readonly Func<byte[], int, byte[]> _decryptPage;
    private readonly Func<byte[], int, byte[]> _encryptPage;
    private readonly string _schemeDescription;

    private OfficeCryptCodecHandler(
        byte[] encodingKey,
        Func<byte[], int, byte[]> decryptPage,
        Func<byte[], int, byte[]> encryptPage,
        string schemeDescription)
    {
        _encodingKey       = encodingKey;
        _decryptPage       = decryptPage;
        _encryptPage       = encryptPage;
        _schemeDescription = schemeDescription;
    }

    /// <summary>Short label for the active scheme — useful in error messages.</summary>
    public string Scheme => _schemeDescription;

    /// <summary>
    /// Inspects <paramref name="headerPage"/> (page 0 of the database) and, if
    /// it advertises a supported Office Crypto scheme, returns a codec configured
    /// with the password. Returns null when the file is not encrypted (the
    /// encoding-key slot at 0x3E is zeroed).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Password verification failed.</exception>
    /// <exception cref="NotSupportedException">Scheme not implemented (RC4, extensible).</exception>
    public static OfficeCryptCodecHandler? FromDbHeader(byte[] headerPage, string password)
    {
        if (headerPage is null || headerPage.Length < CryptStructureOffset + 2)
            return null;

        // Encoding-key slot all-zero → file isn't encrypted; ignore the password.
        bool isBlank = true;
        for (int i = 0; i < EncodingKeyLength; i++)
            if (headerPage[OffsetEncodingKey + i] != 0) { isBlank = false; break; }
        if (isBlank) return null;

        byte[] encodingKey = new byte[EncodingKeyLength];
        Buffer.BlockCopy(headerPage, OffsetEncodingKey, encodingKey, 0, EncodingKeyLength);

        // The encoding key on disk is XOR'd against BASE_HEADER_MASK[38..42] —
        // Jackcess Java does this de-obfuscation at the PageChannel layer before
        // the codec ever sees page 0. We don't have that layer here, so undo
        // the mask in place. (Same bytes for Jet 4 and Jet 12+ — only the trailing
        // 2 bytes of the full mask differ between versions, way past our slot.)
        for (int i = 0; i < EncodingKeyLength; i++)
            encodingKey[i] ^= HeaderMaskAtEncodingKey[i];

        // Re-check after demask: if the unmasked key is all zero, the file is
        // unencrypted (the on-disk masked bytes happened to equal the mask).
        bool unmaskedBlank = true;
        for (int i = 0; i < EncodingKeyLength; i++)
            if (encodingKey[i] != 0) { unmaskedBlank = false; break; }
        if (unmaskedBlank) return null;

        // u16 infoLen — declared length of the EncryptionInfo blob.
        short infoLen = BitConverter.ToInt16(headerPage, CryptStructureOffset);
        int infoStart = CryptStructureOffset + 2;
        int infoEnd   = infoStart + infoLen;
        if (infoEnd > headerPage.Length)
            throw new InvalidDataException("EncryptionInfo struct extends past the header page.");

        // Some unencrypted .accdb files have random non-zero bytes at the
        // encoding-key offset (used for unrelated page scrambling, not crypto).
        // If the EncryptionInfo length is zero or the version field is empty,
        // the file isn't actually encrypted — bail out so the caller can open
        // it with the null codec instead of failing the version dispatch.
        if (infoLen <= 0) return null;

        int p = infoStart;
        int vMajor = BitConverter.ToUInt16(headerPage, p); p += 2;
        int vMinor = BitConverter.ToUInt16(headerPage, p); p += 2;

        if (vMajor == 0 && vMinor == 0) return null;

        byte[] pwdBytes = string.IsNullOrEmpty(password)
            ? Array.Empty<byte>()
            : Encoding.Unicode.GetBytes(password.Length > 255 ? password[..255] : password);

        // ── Dispatch on (vMajor, vMinor) ─────────────────────────────────────
        if (vMajor == 4 && vMinor == 4)
            return BuildAgile(headerPage, p, infoEnd, encodingKey, pwdBytes);

        if ((vMajor == 2 || vMajor == 3 || vMajor == 4) && vMinor == 2)
            return BuildStandardOrRc4(headerPage, p, infoEnd, encodingKey, pwdBytes);

        if ((vMajor == 3 || vMajor == 4) && vMinor == 3)
            throw new NotSupportedException(
                $"Extensible encryption (vMajor={vMajor}, vMinor=3) uses external CryptoAPI providers " +
                "and is not portable; this codec cannot decrypt it.");

        if (vMajor == 1 && vMinor == 1)
            throw new NotSupportedException(
                "RC4 Encryption (vMajor=1, vMinor=1) is used by Office binary documents, " +
                "not .accdb. If you actually have a .accdb here please report it.");

        throw new NotSupportedException(
            $"Unsupported Office encryption version: vMajor={vMajor}, vMinor={vMinor}.");
    }

    public byte[] DecodePage(byte[] rawPage, int pageNumber)
    {
        // Page 0 holds the DB header in plaintext — never decrypt it.
        if (pageNumber == 0) return rawPage;
        return _decryptPage(rawPage, pageNumber);
    }

    public byte[] EncodePage(byte[] page, int pageNumber)
    {
        // Page 0 is the cleartext header — return as-is so the encoding key
        // slot and other plaintext metadata stay readable to anything sniffing.
        if (pageNumber == 0) return page;
        return _encryptPage(page, pageNumber);
    }

    // ────────────────────────────────────────────────────────────────────────
    // ── Agile Encryption (Office 2010+) — MS-OFFCRYPTO 2.3.4.10–13 ──────────
    // ────────────────────────────────────────────────────────────────────────

    private static OfficeCryptCodecHandler BuildAgile(
        byte[] headerPage, int p, int infoEnd, byte[] encodingKey, byte[] pwdBytes)
    {
        // u32 reserved = 0x40, then UTF-8 XML descriptor.
        int reserved = BitConverter.ToInt32(headerPage, p); p += 4;
        if (reserved != 0x40)
            throw new InvalidDataException($"Unexpected Agile reserved value 0x{reserved:X}; expected 0x40.");

        int xmlLen = infoEnd - p;
        string xml = Encoding.UTF8.GetString(headerPage, p, xmlLen);
        var descriptor = AgileDescriptor.Parse(xml);

        // Both halves of the descriptor must use AES-CBC; reject anything else
        // up front rather than silently producing garbage further downstream.
        AgileEngine.ValidateCipher(
            descriptor.PasswordKey.CipherAlgorithm,
            descriptor.PasswordKey.CipherChaining,
            "password key encryptor");
        AgileEngine.ValidateCipher(
            descriptor.KeyData.CipherAlgorithm,
            descriptor.KeyData.CipherChaining,
            "key data");

        if (!AgileEngine.VerifyPassword(descriptor, pwdBytes))
            throw new UnauthorizedAccessException(
                "Incorrect password for Agile-encrypted .accdb (Office 2010+).");

        byte[] keyValue = AgileEngine.DecryptKeyValue(descriptor, pwdBytes);

        return new OfficeCryptCodecHandler(
            encodingKey,
            (page, pageNumber) => AgileEngine.DecryptPage(descriptor, keyValue, encodingKey, page, pageNumber),
            (page, pageNumber) => AgileEngine.EncryptPage(descriptor, keyValue, encodingKey, page, pageNumber),
            "Agile Encryption (Office 2010+)");
    }

    /// <summary>
    /// Subset of the Office Encryption XML descriptor we actually consume.
    /// Captures keyData (master key parameters) and the password key encryptor
    /// (for verifying the password and unwrapping the master key).
    /// </summary>
    internal sealed class AgileDescriptor
    {
        public required KeyParams            KeyData     { get; init; }
        public required PasswordKeyEncryptor PasswordKey { get; init; }

        /// <summary>
        /// Master-key parameters from the &lt;keyData&gt; element of the Agile
        /// encryption descriptor. Drives per-page IV derivation.
        /// </summary>
        public sealed class KeyParams
        {
            public required int    SaltSize        { get; init; }
            public required int    BlockSize       { get; init; }
            public required int    KeyBits         { get; init; }
            public required string CipherAlgorithm { get; init; }   // "AES"
            public required string CipherChaining  { get; init; }   // "ChainingModeCBC"
            public required string HashAlgorithm   { get; init; }   // "SHA512", etc.
            public required byte[] SaltValue       { get; init; }
        }

        public sealed class PasswordKeyEncryptor
        {
            public required int    SaltSize                   { get; init; }
            public required int    BlockSize                  { get; init; }
            public required int    KeyBits                    { get; init; }
            public required int    SpinCount                  { get; init; }
            public required string CipherAlgorithm            { get; init; }
            public required string CipherChaining             { get; init; }
            public required string HashAlgorithm              { get; init; }
            public required byte[] SaltValue                  { get; init; }
            public required byte[] EncryptedVerifierHashInput { get; init; }
            public required byte[] EncryptedVerifierHashValue { get; init; }
            public required byte[] EncryptedKeyValue          { get; init; }
        }

        private const string EncNs = "http://schemas.microsoft.com/office/2006/encryption";
        private const string PwdNs = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

        public static AgileDescriptor Parse(string xml)
        {
            var doc = XDocument.Parse(xml.TrimStart('﻿', ' ', '\r', '\n', '\t'));
            var root = doc.Root
                ?? throw new InvalidDataException("Empty Agile encryption descriptor.");
            if (root.Name.LocalName != "encryption" || root.Name.NamespaceName != EncNs)
                throw new InvalidDataException(
                    $"Unexpected root element '{root.Name}' in encryption descriptor.");

            XElement keyDataEl = root.Element(XName.Get("keyData", EncNs))
                ?? throw new InvalidDataException("Missing <keyData> in encryption descriptor.");
            XElement keyEncryptors = root.Element(XName.Get("keyEncryptors", EncNs))
                ?? throw new InvalidDataException("Missing <keyEncryptors> in encryption descriptor.");

            XElement? passwordEncryptor = keyEncryptors
                .Elements(XName.Get("keyEncryptor", EncNs))
                .Select(e => e.Element(XName.Get("encryptedKey", PwdNs)))
                .FirstOrDefault(e => e is not null);
            if (passwordEncryptor is null)
                throw new InvalidDataException(
                    "Agile descriptor has no <encryptedKey> in the password-protected key encryptor — " +
                    "certificate-only encryption isn't supported.");

            return new AgileDescriptor
            {
                KeyData = new KeyParams
                {
                    SaltSize        = AttrInt   (keyDataEl, "saltSize"),
                    BlockSize       = AttrInt   (keyDataEl, "blockSize"),
                    KeyBits         = AttrInt   (keyDataEl, "keyBits"),
                    CipherAlgorithm = AttrString(keyDataEl, "cipherAlgorithm"),
                    CipherChaining  = AttrString(keyDataEl, "cipherChaining"),
                    HashAlgorithm   = AttrString(keyDataEl, "hashAlgorithm"),
                    SaltValue       = AttrBase64(keyDataEl, "saltValue"),
                },
                PasswordKey = new PasswordKeyEncryptor
                {
                    SaltSize                   = AttrInt   (passwordEncryptor, "saltSize"),
                    BlockSize                  = AttrInt   (passwordEncryptor, "blockSize"),
                    KeyBits                    = AttrInt   (passwordEncryptor, "keyBits"),
                    SpinCount                  = AttrInt   (passwordEncryptor, "spinCount"),
                    CipherAlgorithm            = AttrString(passwordEncryptor, "cipherAlgorithm"),
                    CipherChaining             = AttrString(passwordEncryptor, "cipherChaining"),
                    HashAlgorithm              = AttrString(passwordEncryptor, "hashAlgorithm"),
                    SaltValue                  = AttrBase64(passwordEncryptor, "saltValue"),
                    EncryptedVerifierHashInput = AttrBase64(passwordEncryptor, "encryptedVerifierHashInput"),
                    EncryptedVerifierHashValue = AttrBase64(passwordEncryptor, "encryptedVerifierHashValue"),
                    EncryptedKeyValue          = AttrBase64(passwordEncryptor, "encryptedKeyValue"),
                },
            };
        }

        private static int    AttrInt   (XElement el, string name) => int.Parse(AttrString(el, name));
        private static byte[] AttrBase64(XElement el, string name) => Convert.FromBase64String(AttrString(el, name));
        private static string AttrString(XElement el, string name)
            => el.Attribute(name)?.Value
               ?? throw new InvalidDataException($"Missing required attribute '{name}' on <{el.Name.LocalName}>.");
    }

    private static class AgileEngine
    {
        private static readonly byte[] VerifierInputBlock = { 0xfe, 0xa7, 0xd2, 0x76, 0x3b, 0x4b, 0x9e, 0x79 };
        private static readonly byte[] VerifierValueBlock = { 0xd7, 0xaa, 0x0f, 0x6d, 0x30, 0x61, 0x34, 0x4e };
        private static readonly byte[] KeyValueBlock      = { 0x14, 0x6e, 0x0b, 0xe7, 0xab, 0xac, 0xd0, 0xd6 };

        /// <summary>MS-OFFCRYPTO 2.3.4.11 — purpose-keyed PBKDF derivation.</summary>
        public static byte[] CryptDeriveKey(
            byte[] password, byte[] blockKey, byte[] salt, int spinCount, int keyByteLen,
            string hashAlgorithm)
        {
            byte[] baseHash = HashBytes(hashAlgorithm, salt, password);

            byte[] iterHash = baseHash;
            byte[] indexBuf = new byte[4];
            for (int i = 0; i < spinCount; i++)
            {
                indexBuf[0] = (byte)(i & 0xFF);
                indexBuf[1] = (byte)((i >> 8)  & 0xFF);
                indexBuf[2] = (byte)((i >> 16) & 0xFF);
                indexBuf[3] = (byte)((i >> 24) & 0xFF);
                iterHash = HashBytes(hashAlgorithm, indexBuf, iterHash);
            }

            byte[] finalHash = HashBytes(hashAlgorithm, iterHash, blockKey);
            return FixToLength(finalHash, keyByteLen, 0x36);
        }

        public static byte[] DecryptKeyValue(AgileDescriptor d, byte[] password)
        {
            var pk = d.PasswordKey;
            int keyByteLen = pk.KeyBits / 8;
            byte[] key = CryptDeriveKey(password, KeyValueBlock, pk.SaltValue, pk.SpinCount, keyByteLen, pk.HashAlgorithm);
            return AesCbcDecrypt(key, pk.SaltValue, pk.EncryptedKeyValue);
        }

        public static bool VerifyPassword(AgileDescriptor d, byte[] password)
        {
            var pk = d.PasswordKey;
            int keyByteLen = pk.KeyBits / 8;
            int blockSize  = pk.BlockSize;

            byte[] verifierKey = CryptDeriveKey(password, VerifierInputBlock, pk.SaltValue, pk.SpinCount, keyByteLen, pk.HashAlgorithm);
            byte[] verifier    = AesCbcDecrypt(verifierKey, pk.SaltValue, pk.EncryptedVerifierHashInput);

            byte[] valueKey    = CryptDeriveKey(password, VerifierValueBlock, pk.SaltValue, pk.SpinCount, keyByteLen, pk.HashAlgorithm);
            byte[] storedHash  = AesCbcDecrypt(valueKey, pk.SaltValue, pk.EncryptedVerifierHashValue);

            byte[] testHash = HashBytes(pk.HashAlgorithm, verifier, Array.Empty<byte>());
            if ((testHash.Length % blockSize) != 0)
            {
                int padded = ((testHash.Length + blockSize - 1) / blockSize) * blockSize;
                testHash = FixToLength(testHash, padded, 0);
            }

            return CryptographicOperations.FixedTimeEquals(storedHash, testHash);
        }

        public static byte[] DecryptPage(
            AgileDescriptor d, byte[] keyValue, byte[] encodingKey,
            byte[] cipherPage, int pageNumber)
        {
            byte[] iv = PageIv(d, encodingKey, pageNumber);
            return AesCbcDecrypt(keyValue, iv, cipherPage);
        }

        /// <summary>
        /// Inverse of <see cref="DecryptPage"/> — same key + page-derived IV,
        /// AES-CBC encrypt instead of decrypt. The data-integrity HMAC in the
        /// descriptor is NOT recomputed (it would require deriving the HMAC key
        /// from the master key and re-hashing the whole encrypted stream),
        /// so a re-encrypted file may be rejected by Office's integrity check.
        /// Our own Database.Open(path, password) doesn't enforce HMAC, so the
        /// round-trip through this library is lossless.
        /// </summary>
        public static byte[] EncryptPage(
            AgileDescriptor d, byte[] keyValue, byte[] encodingKey,
            byte[] plainPage, int pageNumber)
        {
            byte[] iv = PageIv(d, encodingKey, pageNumber);
            return AesCbcEncrypt(keyValue, iv, plainPage);
        }

        private static byte[] PageIv(AgileDescriptor d, byte[] encodingKey, int pageNumber)
        {
            byte[] blockBytes = ApplyPageNumber(encodingKey, pageNumber);
            byte[] iv = HashBytes(d.KeyData.HashAlgorithm, d.KeyData.SaltValue, blockBytes);
            return FixToLength(iv, d.KeyData.BlockSize, 0x36);
        }

        public static void ValidateCipher(string cipherAlgorithm, string cipherChaining, string fieldDesc)
        {
            string alg = cipherAlgorithm.ToUpperInvariant();
            string chn = cipherChaining.ToUpperInvariant().Replace("-", "");
            if (alg != "AES")
                throw new NotSupportedException(
                    $"{fieldDesc}: only AES is supported by this codec port; descriptor declared '{cipherAlgorithm}'.");
            if (chn != "CHAININGMODECBC")
                throw new NotSupportedException(
                    $"{fieldDesc}: only CBC chaining is supported by this codec port; descriptor declared '{cipherChaining}'.");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // ── ECMA Standard Encryption (Office 2007) — MS-OFFCRYPTO 2.3.4.5–9 ─────
    // ────────────────────────────────────────────────────────────────────────

    private static OfficeCryptCodecHandler BuildStandardOrRc4(
        byte[] headerPage, int p, int infoEnd, byte[] encodingKey, byte[] pwdBytes)
    {
        // u32 flags (copy of EncryptionHeader.flags) — drives which sub-scheme is in use.
        int flags = BitConverter.ToInt32(headerPage, p); p += 4;

        bool cryptoApi = (flags & FCryptoApiFlag) != 0;
        bool aes       = (flags & FAesFlag)       != 0;
        bool external  = (flags & FExternalFlag)  != 0;

        if (external)
            throw new NotSupportedException(
                "External cryptographic providers are not portable; this codec cannot decrypt them.");
        if (!cryptoApi)
            throw new NotSupportedException(
                "Pre-CryptoAPI encryption variants (XOR / Office 97 RC4) aren't supported in .accdb.");

        if (aes)
        {
            // ECMA Standard Encryption (AES) — MS-OFFCRYPTO §2.3.4.5–9.
            // Only parse with requireAes=true on this branch; the non-AES branch
            // defers to BuildRc4OrNonStandard which has its own try/catch ladder.
            var info = StandardEncryptionInfo.Read(headerPage, p, infoEnd, requireAes: true);
            var engine = new StandardEngine(info);
            if (!engine.VerifyPassword(pwdBytes))
                throw new UnauthorizedAccessException(
                    "Incorrect password for ECMA Standard-encrypted .accdb (Office 2007).");
            return new OfficeCryptCodecHandler(
                encodingKey,
                (page, pageNumber) => engine.DecryptPage(pwdBytes, encodingKey, page, pageNumber),
                (page, pageNumber) => engine.EncryptPage(pwdBytes, encodingKey, page, pageNumber),
                $"ECMA Standard Encryption (Office 2007, AES-{info.KeySizeBits})");
        }

        return BuildRc4OrNonStandard(headerPage, p, infoEnd, encodingKey, pwdBytes);
    }

    /// <summary>
    /// Handles the flags-say-RC4 dispatch branch. Tries RC4 CryptoAPI first;
    /// if the header turns out to actually declare AES (a Microsoft quirk
    /// known as the "NonStandard" provider), falls back to AES with zero
    /// hash iterations — matches upstream Jackcess-Encrypt behaviour.
    /// </summary>
    private static OfficeCryptCodecHandler BuildRc4OrNonStandard(
        byte[] headerPage, int p, int infoEnd, byte[] encodingKey, byte[] pwdBytes)
    {
        Exception? rc4Failure = null;
        try
        {
            var info = StandardEncryptionInfo.Read(headerPage, p, infoEnd, requireAes: false);
            var rc4 = new Rc4CryptoApiEngine(info);
            if (!rc4.VerifyPassword(pwdBytes))
                throw new UnauthorizedAccessException(
                    "Incorrect password for RC4 CryptoAPI-encrypted .accdb (Office 2002–2003).");
            return new OfficeCryptCodecHandler(
                encodingKey,
                (page, pageNumber) => rc4.CryptPage(pwdBytes, encodingKey, page, pageNumber),
                (page, pageNumber) => rc4.CryptPage(pwdBytes, encodingKey, page, pageNumber),
                $"RC4 CryptoAPI Encryption (RC4-{info.KeySizeBits})");
        }
        catch (NotSupportedException ex)
        {
            // Header actually claims AES despite the flags — fall through to
            // NonStandard (AES with 0 iterations).
            rc4Failure = ex;
        }

        try
        {
            var info = StandardEncryptionInfo.Read(headerPage, p, infoEnd, requireAes: true);
            var engine = new StandardEngine(info, iterations: 0);
            if (!engine.VerifyPassword(pwdBytes))
                throw new UnauthorizedAccessException(
                    "Incorrect password for non-standard AES-encrypted .accdb (compat mode 0).");
            return new OfficeCryptCodecHandler(
                encodingKey,
                (page, pageNumber) => engine.DecryptPage(pwdBytes, encodingKey, page, pageNumber),
                (page, pageNumber) => engine.EncryptPage(pwdBytes, encodingKey, page, pageNumber),
                $"Non-Standard AES Encryption (compat mode 0, AES-{info.KeySizeBits})");
        }
        catch (Exception fallbackEx) when (fallbackEx is not UnauthorizedAccessException)
        {
            // Neither path is structurally valid — re-throw the original RC4 error
            // since that was the primary candidate the flags pointed at.
            throw rc4Failure ?? fallbackEx;
        }
    }

    /// <summary>
    /// Parsed view of an ECMA Standard EncryptionInfo struct (MS-OFFCRYPTO
    /// §2.3.2 EncryptionHeader + §2.3.3 EncryptionVerifier).
    /// </summary>
    private sealed class StandardEncryptionInfo
    {
        public required int    KeySizeBits             { get; init; }
        public required byte[] Salt                    { get; init; }
        public required byte[] EncryptedVerifier       { get; init; }   // 16 bytes
        public required int    VerifierHashSize        { get; init; }
        public required byte[] EncryptedVerifierHash   { get; init; }   // 32 bytes for AES

        public int KeySizeBytes => KeySizeBits / 8;

        public static StandardEncryptionInfo Read(byte[] page, int pos, int end, bool requireAes = true)
        {
            // EncryptionInfo (vMinor=2) layout after the flags word we already consumed:
            //   u32 headerLen
            //   EncryptionHeader (headerLen bytes)
            //   EncryptionVerifier
            int headerLen = BitConverter.ToInt32(page, pos); pos += 4;
            int headerEnd = pos + headerLen;
            if (headerEnd > end)
                throw new InvalidDataException(
                    "EncryptionHeader length extends past the EncryptionInfo struct.");

            // EncryptionHeader: flags u32, sizeExtra u32, algId u32, algIdHash u32,
            //                   keySize u32, providerType u32, reserved1 u32,
            //                   reserved2 u32, cspName (UTF-16LE, NUL-terminated).
            int hdrFlags  = BitConverter.ToInt32(page, pos); pos += 4;
            /* sizeExtra */ pos += 4;
            int algId     = BitConverter.ToInt32(page, pos); pos += 4;
            int algIdHash = BitConverter.ToInt32(page, pos); pos += 4;
            int keySize   = BitConverter.ToInt32(page, pos); pos += 4;
            /* providerType */ pos += 4;
            /* reserved1 */    pos += 4;
            /* reserved2 */    pos += 4;
            pos = headerEnd;   // skip the rest of the header (CSP name, possible trailing bytes)

            int resolvedKey = ResolveKeySize(algId, keySize, hdrFlags);
            if (requireAes)
                ValidateStandardHeader(algId, algIdHash, resolvedKey);
            else
                ValidateRc4Header(algId, algIdHash, resolvedKey);

            // EncryptionVerifier (MS-OFFCRYPTO 2.3.3):
            //   u32 saltSize (always 16 in Standard)
            //   salt[saltSize]
            //   encryptedVerifier[16]
            //   u32 verifierHashSize  (usually 20 for SHA-1)
            //   encryptedVerifierHash[len-based-on-cryptoAlg]
            int saltSize = BitConverter.ToInt32(page, pos); pos += 4;
            if (saltSize != 16)
                throw new InvalidDataException($"Unexpected salt size {saltSize}; ECMA Standard requires 16.");
            byte[] salt = Slice(page, pos, 16); pos += 16;
            byte[] encVerifier = Slice(page, pos, 16); pos += 16;
            int verifierHashSize = BitConverter.ToInt32(page, pos); pos += 4;
            int encVerifierHashLen = EncryptedVerifierHashLen(algId);
            byte[] encVerifierHash = Slice(page, pos, encVerifierHashLen);

            return new StandardEncryptionInfo
            {
                KeySizeBits           = resolvedKey,
                Salt                  = salt,
                EncryptedVerifier     = encVerifier,
                VerifierHashSize      = verifierHashSize,
                EncryptedVerifierHash = encVerifierHash,
            };
        }

        private static int ResolveKeySize(int algId, int declaredKeySize, int flags)
        {
            // If a non-zero key size is declared we trust it; otherwise fall back to
            // the algorithm's documented default (MS-OFFCRYPTO 2.3.2 Table).
            if (declaredKeySize != 0) return declaredKeySize;
            if (algId == AlgIdAes128) return 128;
            if (algId == AlgIdAes192) return 192;
            if (algId == AlgIdAes256) return 256;
            // ALGID_FLAGS (0) with FAES → AES-128 by default (CryptoAPI default).
            if (algId == 0 && (flags & FAesFlag) != 0) return 128;
            // RC4 default: Base CryptoAPI provider → 40 bits, Strong → 128 bits.
            // MS-OFFCRYPTO 2.3.2 says use 0x28 (40) when unspecified.
            if (algId == AlgIdRc4 || (algId == 0 && (flags & FAesFlag) == 0)) return 40;
            throw new InvalidDataException(
                $"Could not determine encryption key size from algId=0x{algId:X} keySize={declaredKeySize}.");
        }

        private static void ValidateStandardHeader(int algId, int algIdHash, int keySize)
        {
            if (algId != AlgIdAes128 && algId != AlgIdAes192 && algId != AlgIdAes256 && algId != 0)
                throw new NotSupportedException(
                    $"ECMA Standard Encryption requires AES (algId 0x660E/660F/6610); got 0x{algId:X}." +
                    (algId == AlgIdRc4 ? " RC4 CryptoAPI is not implemented." : ""));
            if (algIdHash != HashAlgIdSha1 && algIdHash != 0)
                throw new NotSupportedException(
                    $"ECMA Standard Encryption requires SHA-1 hash (algIdHash 0x8004); got 0x{algIdHash:X}.");
            if (keySize != 128 && keySize != 192 && keySize != 256)
                throw new InvalidDataException($"Unexpected AES key size {keySize}.");
        }

        private static void ValidateRc4Header(int algId, int algIdHash, int keySize)
        {
            if (algId != AlgIdRc4 && algId != 0)
                throw new NotSupportedException(
                    $"RC4 CryptoAPI Encryption requires RC4 (algId 0x6801); got 0x{algId:X}.");
            if (algIdHash != HashAlgIdSha1 && algIdHash != 0)
                throw new NotSupportedException(
                    $"RC4 CryptoAPI Encryption requires SHA-1 hash (algIdHash 0x8004); got 0x{algIdHash:X}.");
            // RC4 key sizes range 40..128 bits, in multiples of 8 (MS-OFFCRYPTO 2.3.2).
            if (keySize < 40 || keySize > 128 || (keySize % 8) != 0)
                throw new InvalidDataException($"Unexpected RC4 key size {keySize} bits.");
        }

        private static int EncryptedVerifierHashLen(int algId) => algId switch
        {
            AlgIdAes128 => 32,
            AlgIdAes192 => 32,
            AlgIdAes256 => 32,
            _           => 32,    // AES variants all share the 32-byte verifier-hash slot
        };

        private static byte[] Slice(byte[] src, int start, int len)
        {
            byte[] dst = new byte[len];
            Buffer.BlockCopy(src, start, dst, 0, len);
            return dst;
        }
    }

    private sealed class StandardEngine
    {
        private const int DefaultIterations = 50_000;
        private readonly StandardEncryptionInfo _info;
        private readonly int _iterations;
        private byte[]? _baseHashCache;

        public StandardEngine(StandardEncryptionInfo info, int iterations = DefaultIterations)
        {
            _info = info;
            _iterations = iterations;
        }

        public bool VerifyPassword(byte[] password)
        {
            // MS-OFFCRYPTO 2.3.4.9 — verify by decrypting with the page-0-shaped
            // key (block = 4 zero bytes, NOT XOR'd with the encoding key) and
            // re-hashing the cleartext verifier.
            byte[] zeroBlock = new byte[4];
            byte[] cipherKey = ComputeEncryptionKey(password, zeroBlock);
            byte[] verifier  = AesEcbDecrypt(cipherKey, _info.EncryptedVerifier);
            byte[] storedHash = AesEcbDecrypt(cipherKey, _info.EncryptedVerifierHash);
            storedHash = FixToLength(storedHash, _info.VerifierHashSize, 0);

            byte[] testHash = FixToLength(HashBytes("SHA1", verifier, Array.Empty<byte>()),
                                          _info.VerifierHashSize, 0);
            return CryptographicOperations.FixedTimeEquals(storedHash, testHash);
        }

        public byte[] DecryptPage(byte[] password, byte[] encodingKey, byte[] cipherPage, int pageNumber)
        {
            byte[] blockBytes = ApplyPageNumber(encodingKey, pageNumber);
            byte[] key        = ComputeEncryptionKey(password, blockBytes);
            return AesEcbDecrypt(key, cipherPage);
        }

        public byte[] EncryptPage(byte[] password, byte[] encodingKey, byte[] plainPage, int pageNumber)
        {
            byte[] blockBytes = ApplyPageNumber(encodingKey, pageNumber);
            byte[] key        = ComputeEncryptionKey(password, blockBytes);
            return AesEcbEncrypt(key, plainPage);
        }

        /// <summary>
        /// Derives the per-block AES key (MS-OFFCRYPTO 2.3.4.7). The base hash
        /// (SHA1(salt || password)) is cached so the 50 000-iteration loop only
        /// runs once per password — without this, every page decode would re-run
        /// the spin loop.
        /// </summary>
        private byte[] ComputeEncryptionKey(byte[] password, byte[] blockBytes)
        {
            byte[] baseHash = _baseHashCache ??= HashBytes("SHA1", _info.Salt, password);

            byte[] iterHash = baseHash;
            byte[] indexBuf = new byte[4];
            for (int i = 0; i < _iterations; i++)
            {
                indexBuf[0] = (byte)(i & 0xFF);
                indexBuf[1] = (byte)((i >> 8)  & 0xFF);
                indexBuf[2] = (byte)((i >> 16) & 0xFF);
                indexBuf[3] = (byte)((i >> 24) & 0xFF);
                iterHash = HashBytes("SHA1", indexBuf, iterHash);
            }

            byte[] finalHash = HashBytes("SHA1", iterHash, blockBytes);

            // Expand finalHash → 2 × hashLen by XOR-padding twice (2.3.4.7).
            byte[] x1 = HashBytes("SHA1", GenXBytes(finalHash, 0x36), Array.Empty<byte>());
            byte[] x2 = HashBytes("SHA1", GenXBytes(finalHash, 0x5C), Array.Empty<byte>());

            byte[] combined = new byte[x1.Length + x2.Length];
            Buffer.BlockCopy(x1, 0, combined, 0,         x1.Length);
            Buffer.BlockCopy(x2, 0, combined, x1.Length, x2.Length);

            return FixToLength(combined, _info.KeySizeBytes, 0);
        }

        private static byte[] GenXBytes(byte[] finalHash, byte fill)
        {
            byte[] x = new byte[64];
            for (int i = 0; i < x.Length; i++) x[i] = fill;
            for (int i = 0; i < finalHash.Length && i < x.Length; i++)
                x[i] ^= finalHash[i];
            return x;
        }

        private static byte[] AesEcbDecrypt(byte[] key, byte[] cipher)
        {
            using var aes = Aes.Create();
            aes.Key     = key;
            aes.Mode    = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var dec = aes.CreateDecryptor();
            return dec.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        private static byte[] AesEcbEncrypt(byte[] key, byte[] plain)
        {
            using var aes = Aes.Create();
            aes.Key     = key;
            aes.Mode    = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using var enc = aes.CreateEncryptor();
            return enc.TransformFinalBlock(plain, 0, plain.Length);
        }
    }

    /// <summary>
    /// RC4 CryptoAPI Encryption engine (MS-OFFCRYPTO §2.3.5.2). Per-page key is
    /// SHA-1(baseHash || pageBlock) truncated to the declared key size.
    /// For 40-bit "export-grade" keys the actual cipher key is zero-padded to
    /// 128 bits before being passed to RC4 — a Microsoft quirk preserved here.
    /// </summary>
    private sealed class Rc4CryptoApiEngine
    {
        private readonly StandardEncryptionInfo _info;
        private byte[]? _baseHashCache;

        public Rc4CryptoApiEngine(StandardEncryptionInfo info) => _info = info;

        public bool VerifyPassword(byte[] password)
        {
            // Page-0 key (block = 4 zero bytes). RC4 keystream continues across
            // the two verifier blobs — initialise the cipher once and decrypt
            // verifier + verifierHash in sequence (same keystream).
            byte[] key = ComputeKey(password, new byte[4]);
            byte[] stream = Rc4(key, _info.EncryptedVerifier.Length + _info.EncryptedVerifierHash.Length);

            byte[] verifier = new byte[_info.EncryptedVerifier.Length];
            for (int i = 0; i < verifier.Length; i++)
                verifier[i] = (byte)(_info.EncryptedVerifier[i] ^ stream[i]);

            byte[] storedHash = new byte[_info.EncryptedVerifierHash.Length];
            for (int i = 0; i < storedHash.Length; i++)
                storedHash[i] = (byte)(_info.EncryptedVerifierHash[i] ^ stream[verifier.Length + i]);

            storedHash = FixToLength(storedHash, _info.VerifierHashSize, 0);
            byte[] testHash = FixToLength(HashBytes("SHA1", verifier, Array.Empty<byte>()),
                                          _info.VerifierHashSize, 0);
            return CryptographicOperations.FixedTimeEquals(storedHash, testHash);
        }

        public byte[] CryptPage(byte[] password, byte[] encodingKey, byte[] page, int pageNumber)
        {
            byte[] block = ApplyPageNumber(encodingKey, pageNumber);
            byte[] key   = ComputeKey(password, block);
            byte[] keystream = Rc4(key, page.Length);
            byte[] result = new byte[page.Length];
            for (int i = 0; i < page.Length; i++)
                result[i] = (byte)(page[i] ^ keystream[i]);
            return result;
        }

        /// <summary>
        /// MS-OFFCRYPTO 2.3.5.2: encKey = SHA1(baseHash || block), truncated
        /// to keyByteSize. For 40-bit keys, pad with zeroes to 128 bits before
        /// handing to RC4 (CryptoAPI behaviour for "export-grade" RC4).
        /// </summary>
        private byte[] ComputeKey(byte[] password, byte[] block)
        {
            byte[] baseHash = _baseHashCache ??= HashBytes("SHA1", _info.Salt, password);
            byte[] full = HashBytes("SHA1", baseHash, block);
            byte[] key = FixToLength(full, _info.KeySizeBytes, 0);
            if (_info.KeySizeBits == 40)
                key = FixToLength(key, 16, 0);   // pad 40-bit → 128-bit
            return key;
        }

        /// <summary>
        /// Standalone RC4 KSA + PRGA emitting <paramref name="length"/> bytes
        /// of keystream. Mirrors <c>JetCryptCodecHandler.Rc4Keystream</c> but
        /// duplicated here to keep the Codecs files self-contained.
        /// </summary>
        private static byte[] Rc4(byte[] key, int length)
        {
            var s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            int j = 0;
            for (int i = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 0xFF;
                (s[i], s[j]) = (s[j], s[i]);
            }
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
    }

    // ────────────────────────────────────────────────────────────────────────
    // ── Shared crypto helpers ───────────────────────────────────────────────
    // ────────────────────────────────────────────────────────────────────────

    private static byte[] ApplyPageNumber(byte[] encodingKey, int pageNumber)
    {
        byte[] block = new byte[EncodingKeyLength];
        block[0] = (byte)((pageNumber       & 0xFF) ^ encodingKey[0]);
        block[1] = (byte)(((pageNumber>>8 ) & 0xFF) ^ encodingKey[1]);
        block[2] = (byte)(((pageNumber>>16) & 0xFF) ^ encodingKey[2]);
        block[3] = (byte)(((pageNumber>>24) & 0xFF) ^ encodingKey[3]);
        return block;
    }

    private static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] cipher)
    {
        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv.Length == 16 ? iv : FixToLength(iv, 16, 0x36);
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var dec = aes.CreateDecryptor();
        return dec.TransformFinalBlock(cipher, 0, cipher.Length);
    }

    private static byte[] AesCbcEncrypt(byte[] key, byte[] iv, byte[] plain)
    {
        using var aes = Aes.Create();
        aes.Key     = key;
        aes.IV      = iv.Length == 16 ? iv : FixToLength(iv, 16, 0x36);
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var enc = aes.CreateEncryptor();
        return enc.TransformFinalBlock(plain, 0, plain.Length);
    }

    /// <summary>
    /// Hashes <c>a || b</c> with the named algorithm. Uses .NET 8 one-shot
    /// static methods (<see cref="SHA512.HashData(System.ReadOnlySpan{byte})"/>
    /// etc.) so the spin loop's 100,000 iterations don't pay for HashAlgorithm
    /// instance allocation per pass and no TransformBlock state is involved.
    /// </summary>
    private static byte[] HashBytes(string algorithm, byte[] a, byte[] b)
    {
        // Concat once into a single buffer so we can use the static one-shot.
        byte[] combined;
        if (a.Length == 0)      combined = b;
        else if (b.Length == 0) combined = a;
        else
        {
            combined = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, combined, 0,         a.Length);
            Buffer.BlockCopy(b, 0, combined, a.Length,  b.Length);
        }
        return HashOneShot(algorithm, combined);
    }

    private static byte[] HashOneShot(string algorithm, byte[] data) =>
        algorithm.ToUpperInvariant().Replace("-", "").Replace("_", "") switch
        {
            "SHA1"      => SHA1.HashData(data),
            "SHA256"    => SHA256.HashData(data),
            "SHA384"    => SHA384.HashData(data),
            "SHA512"    => SHA512.HashData(data),
            "MD5"       => MD5.HashData(data),
            _ => throw new NotSupportedException(
                   $"Hash algorithm '{algorithm}' is not supported by the Office Crypt codec port. " +
                   "Supported: SHA1, SHA256, SHA384, SHA512, MD5."),
        };

    private static byte[] FixToLength(byte[] bytes, int len, byte padByte)
    {
        if (bytes.Length == len) return bytes;
        byte[] result = new byte[len];
        Buffer.BlockCopy(bytes, 0, result, 0, Math.Min(bytes.Length, len));
        if (bytes.Length < len)
            for (int i = bytes.Length; i < len; i++) result[i] = padByte;
        return result;
    }
}
