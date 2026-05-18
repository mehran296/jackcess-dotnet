namespace JackcessDotNet;

/// <summary>
/// Maps .NET CLR types onto Jet <see cref="DataType"/>s and builds the matching
/// <see cref="Column"/> definitions. Used by the import wrappers
/// (<see cref="DatabaseImporter"/>) so callers can hand over a
/// <c>DataTable</c>/<c>DataSet</c>/<c>IEnumerable&lt;T&gt;</c> and get a sensibly-typed
/// Access schema without spelling it out themselves.
/// </summary>
public static class TypeMapper
{
    /// <summary>
    /// Default text length for inferred string columns when no
    /// <see cref="System.ComponentModel.DataAnnotations.MaxLengthAttribute"/>
    /// or <c>DataColumn.MaxLength</c> is supplied. Strings longer than this at
    /// import time will overflow the Text column's byte limit — use a Memo
    /// instead via <see cref="ColumnFromClrType"/>'s explicit hints.
    /// </summary>
    public const int DefaultTextCharLength = 255;

    /// <summary>Threshold beyond which strings flip to Memo (no inline limit).</summary>
    public const int MemoLengthThreshold = 255;

    /// <summary>
    /// Resolves <paramref name="clrType"/> (incl. Nullable&lt;T&gt;) to a Jet
    /// <see cref="DataType"/>. Returns null when the type can't be represented.
    /// </summary>
    public static DataType? MapClrType(Type clrType)
    {
        if (clrType is null) return null;

        // Unwrap Nullable<T> — Access columns are nullable by default.
        Type t = Nullable.GetUnderlyingType(clrType) ?? clrType;

        // Enums: take the underlying primitive.
        if (t.IsEnum) t = Enum.GetUnderlyingType(t);

        if (t == typeof(bool))     return DataType.Boolean;
        if (t == typeof(byte))     return DataType.Byte;
        if (t == typeof(sbyte))    return DataType.Byte;       // signed 8-bit → unsigned Byte
        if (t == typeof(short))    return DataType.Int;
        if (t == typeof(ushort))   return DataType.Int;
        if (t == typeof(int))      return DataType.Long;
        if (t == typeof(uint))     return DataType.Long;
        if (t == typeof(long))     return DataType.Long;       // truncated to 32-bit Long
        if (t == typeof(ulong))    return DataType.Long;
        if (t == typeof(float))    return DataType.Float;
        if (t == typeof(double))   return DataType.Double;
        if (t == typeof(decimal))  return DataType.Money;      // 4-decimal precision is usually enough
        if (t == typeof(DateTime)) return DataType.ShortDateTime;
        if (t == typeof(DateTimeOffset)) return DataType.ShortDateTime;
        if (t == typeof(Guid))     return DataType.Guid;
        if (t == typeof(string))   return DataType.Text;       // caller may promote to Memo on length
        if (t == typeof(char))     return DataType.Text;
        if (t == typeof(byte[]))   return DataType.Binary;     // caller may promote to OLE on length

        return null;
    }

    /// <summary>
    /// Builds a <see cref="Column"/> from a property/column name + CLR type, with
    /// optional max-length hint and autonumber flag. String fields longer than
    /// <see cref="MemoLengthThreshold"/> chars are promoted to <see cref="DataType.Memo"/>;
    /// byte arrays longer than 255 bytes are promoted to <see cref="DataType.Ole"/>.
    /// Returns null if the type isn't mappable.
    /// </summary>
    public static Column? ColumnFromClrType(
        string name, Type clrType, int? maxLength = null, bool autoNumber = false)
    {
        var dt = MapClrType(clrType);
        if (dt is null) return null;

        var cb = new ColumnBuilder(name, dt.Value);

        // String/byte-array promotion based on max length.
        if (dt == DataType.Text && maxLength is int charCap && charCap > MemoLengthThreshold)
        {
            cb = new ColumnBuilder(name, DataType.Memo);
        }
        else if (dt == DataType.Text)
        {
            cb.MaxLength(maxLength ?? DefaultTextCharLength);
        }
        else if (dt == DataType.Binary && maxLength is int byteCap && byteCap > 255)
        {
            cb = new ColumnBuilder(name, DataType.Ole);
        }
        else if (dt == DataType.Binary)
        {
            cb.WithLength(maxLength ?? 50);
        }

        if (autoNumber && cb.Build().DataType == DataType.Long)
            cb.WithAutoNumber();

        return cb.Build();
    }

    /// <summary>
    /// Converts an arbitrary boxed value to the form expected by <see cref="RowEncoder"/>
    /// for <paramref name="targetType"/>. Handles widening (long→int), nullable
    /// unwrapping, enum-to-underlying, and DBNull.
    /// </summary>
    public static object? CoerceForJet(object? value, DataType targetType)
    {
        if (value is null || value == DBNull.Value) return null;

        // Unwrap nullable.
        if (value.GetType().IsGenericType
            && value.GetType().GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            // Boxed Nullable<T> always boxes as the underlying value or null — covered above.
        }

        // Enum → underlying.
        if (value.GetType().IsEnum)
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()));

        return targetType switch
        {
            DataType.Boolean       => Convert.ToBoolean(value),
            DataType.Byte          => Convert.ToByte(value),
            DataType.Int           => Convert.ToInt16(value),
            DataType.Long          => Convert.ToInt32(value),
            DataType.Float         => Convert.ToSingle(value),
            DataType.Double        => Convert.ToDouble(value),
            DataType.Money         => Convert.ToDecimal(value),
            DataType.Numeric       => Convert.ToDecimal(value),
            DataType.ShortDateTime => value is DateTimeOffset dto
                                        ? dto.UtcDateTime
                                        : Convert.ToDateTime(value),
            DataType.Guid          => value is Guid g ? g : new Guid(value.ToString()!),
            DataType.Text          => Convert.ToString(value),
            DataType.Memo          => Convert.ToString(value),
            DataType.Binary        => value is byte[] b ? b : throw new ArgumentException(
                                        $"Cannot coerce {value.GetType()} into Binary column"),
            DataType.Ole           => value is byte[] o ? o : throw new ArgumentException(
                                        $"Cannot coerce {value.GetType()} into OLE column"),
            _                      => value,
        };
    }
}
