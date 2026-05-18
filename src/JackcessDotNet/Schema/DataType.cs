namespace JackcessDotNet;

/// <summary>
/// Microsoft Access / Jet data types.
/// Byte values match the Jet wire format stored in TDEF column descriptors.
/// </summary>
public enum DataType : byte
{
    /// <summary>Boolean (1 bit, stored as 1 byte in fixed column area)</summary>
    Boolean = 0x01,

    /// <summary>8-bit unsigned integer</summary>
    Byte = 0x02,

    /// <summary>16-bit signed integer</summary>
    Int = 0x03,

    /// <summary>32-bit signed integer (Access "Long Integer")</summary>
    Long = 0x04,

    /// <summary>64-bit fixed-point currency (4 decimal places)</summary>
    Money = 0x05,

    /// <summary>32-bit IEEE floating point</summary>
    Float = 0x06,

    /// <summary>64-bit IEEE floating point (Access "Double")</summary>
    Double = 0x07,

    /// <summary>OLE Automation date (64-bit double, days since 30-Dec-1899)</summary>
    ShortDateTime = 0x08,

    /// <summary>Variable-length binary, max 255 bytes</summary>
    Binary = 0x09,

    /// <summary>Variable-length Unicode text, max 255 chars (stored as UTF-16LE * compressed)</summary>
    Text = 0x0A,

    /// <summary>OLE object (large binary, possibly external)</summary>
    Ole = 0x0B,

    /// <summary>Memo / long text (large text, possibly external)</summary>
    Memo = 0x0C,

    /// <summary>GUID (16-byte identifier)</summary>
    Guid = 0x0F,

    /// <summary>Fixed-precision decimal (17 bytes, max 28 significant digits)</summary>
    Numeric = 0x10,

    /// <summary>Complex (Access 2007+, not supported in Jet 4)</summary>
    Complex = 0x12,
}

/// <summary>
/// Extension methods and metadata for <see cref="DataType"/>.
/// </summary>
public static class DataTypeExtensions
{
    private static readonly Dictionary<DataType, DataTypeInfo> _info = new()
    {
        [DataType.Boolean]       = new(FixedSize: 1,  IsVariable: false, IsLong: false),
        [DataType.Byte]          = new(FixedSize: 1,  IsVariable: false, IsLong: false),
        [DataType.Int]           = new(FixedSize: 2,  IsVariable: false, IsLong: false),
        [DataType.Long]          = new(FixedSize: 4,  IsVariable: false, IsLong: false),
        [DataType.Money]         = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.Float]         = new(FixedSize: 4,  IsVariable: false, IsLong: false),
        [DataType.Double]        = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.ShortDateTime] = new(FixedSize: 8,  IsVariable: false, IsLong: false),
        [DataType.Guid]          = new(FixedSize: 16, IsVariable: false, IsLong: false),
        [DataType.Numeric]       = new(FixedSize: 17, IsVariable: false, IsLong: false),
        [DataType.Binary]        = new(FixedSize: 0,  IsVariable: true,  IsLong: false, MaxSize: 255),
        [DataType.Text]          = new(FixedSize: 0,  IsVariable: true,  IsLong: false, MaxSize: 510),  // 255 chars * 2 bytes UTF-16
        [DataType.Ole]           = new(FixedSize: 0,  IsVariable: true,  IsLong: true),
        [DataType.Memo]          = new(FixedSize: 0,  IsVariable: true,  IsLong: true),
        // Complex columns (multi-value fields, attachments, version history): the
        // value lives in a separate complex-column-data table; what's stored in
        // the row is a 4-byte ID. Decoding the linked table isn't implemented yet,
        // but registering the type as "fixed 4-byte" lets the column be parsed and
        // tables that contain it can still be opened + iterated.
        [DataType.Complex]       = new(FixedSize: 4,  IsVariable: false, IsLong: false),
    };

    private static readonly DataTypeInfo UnknownInfo =
        new(FixedSize: 0, IsVariable: true, IsLong: false);

    /// <summary>
    /// Returns the layout info for <paramref name="dt"/>. Unknown types (newer
    /// Access generations introduce new codes from time to time) fall back to
    /// a variable-length placeholder so the column can be skipped rather than
    /// crashing the whole TDEF parse.
    /// </summary>
    public static DataTypeInfo GetInfo(this DataType dt)
        => _info.TryGetValue(dt, out var info) ? info : UnknownInfo;

    public static bool IsVariableLength(this DataType dt) => GetInfo(dt).IsVariable;
    public static bool IsFixedLength(this DataType dt) => !GetInfo(dt).IsVariable;
    public static bool IsLongValue(this DataType dt) => GetInfo(dt).IsLong;

    /// <summary>Fixed storage size in bytes; 0 for variable-length types.</summary>
    public static int GetFixedSize(this DataType dt) => GetInfo(dt).FixedSize;

    /// <summary>
    /// Default column size: for fixed types returns the fixed size,
    /// for Text returns the default max char count (50), for Binary returns 50.
    /// </summary>
    public static int GetDefaultSize(this DataType dt)
    {
        var info = GetInfo(dt);
        if (!info.IsVariable) return info.FixedSize;
        if (dt == DataType.Text) return 100; // 50 chars * 2 bytes
        if (dt == DataType.Binary) return 50;
        return 0; // long types
    }

    public static int GetMaxSize(this DataType dt) => GetInfo(dt).MaxSize;
}

/// <param name="FixedSize">Storage size in bytes for fixed-length types (0 if variable).</param>
/// <param name="IsVariable">True if the column stores data in the variable-length area.</param>
/// <param name="IsLong">True for MEMO/OLE types stored in overflow pages.</param>
/// <param name="MaxSize">Max bytes for variable-length types (0 = unlimited/overflow).</param>
public record DataTypeInfo(int FixedSize, bool IsVariable, bool IsLong, int MaxSize = 0);
