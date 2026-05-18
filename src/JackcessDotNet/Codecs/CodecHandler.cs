namespace JackcessDotNet;

/// <summary>
/// Per-page encode/decode transformation applied by <see cref="PageFile"/> on
/// every read and write. The default <see cref="NullCodecHandler"/> is the
/// identity. For encrypted Access files (password-protected .accdb, MSISAM),
/// an implementation would XOR each page against a key-derived keystream.
///
/// Today we ship only the null codec; encrypted files are detected in
/// <see cref="Database.Open(string)"/> and rejected with a clear error so users
/// don't get cryptic page-parse failures downstream.
/// </summary>
public interface ICodecHandler
{
    /// <summary>
    /// Returns the plaintext page bytes given the raw bytes read from disk.
    /// <paramref name="pageNumber"/> is supplied because real codecs derive the
    /// page-specific keystream from the page index.
    /// </summary>
    byte[] DecodePage(byte[] rawPage, int pageNumber);

    /// <summary>Inverse of <see cref="DecodePage"/>: returns the bytes to write to disk.</summary>
    byte[] EncodePage(byte[] page, int pageNumber);
}

/// <summary>Identity codec used for unencrypted .mdb / .accdb files.</summary>
public sealed class NullCodecHandler : ICodecHandler
{
    public static readonly NullCodecHandler Instance = new();
    public byte[] DecodePage(byte[] rawPage, int pageNumber) => rawPage;
    public byte[] EncodePage(byte[] page,    int pageNumber) => page;
}
