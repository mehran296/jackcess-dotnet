using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Data;
using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tests for the <see cref="DatabaseImporter"/> wrapper that turns a
/// <see cref="DataTable"/>, <see cref="DataSet"/>, or any
/// <c>IEnumerable&lt;T&gt;</c> into an Access table without the caller
/// hand-writing column builders.
/// </summary>
public sealed class DatabaseImporterTests : IDisposable
{
    private readonly string _path;
    public DatabaseImporterTests()
        => _path = Path.Combine(Path.GetTempPath(), $"imp_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    // ── TypeMapper unit-level checks ─────────────────────────────────────────

    [Theory]
    [InlineData(typeof(bool),           DataType.Boolean)]
    [InlineData(typeof(byte),           DataType.Byte)]
    [InlineData(typeof(short),          DataType.Int)]
    [InlineData(typeof(int),            DataType.Long)]
    [InlineData(typeof(long),           DataType.Long)]
    [InlineData(typeof(float),          DataType.Float)]
    [InlineData(typeof(double),         DataType.Double)]
    [InlineData(typeof(decimal),        DataType.Money)]
    [InlineData(typeof(DateTime),       DataType.ShortDateTime)]
    [InlineData(typeof(DateTimeOffset), DataType.ShortDateTime)]
    [InlineData(typeof(Guid),           DataType.Guid)]
    [InlineData(typeof(string),         DataType.Text)]
    [InlineData(typeof(byte[]),         DataType.Binary)]
    public void TypeMapper_MapsPrimitivesAndNullables(Type clr, DataType expected)
    {
        Assert.Equal(expected, TypeMapper.MapClrType(clr));
        // Nullable<T> wrapper should resolve to the same Jet type.
        if (clr.IsValueType)
        {
            var nullable = typeof(Nullable<>).MakeGenericType(clr);
            Assert.Equal(expected, TypeMapper.MapClrType(nullable));
        }
    }

    [Fact]
    public void TypeMapper_Enum_UnwrapsToUnderlyingType()
    {
        Assert.Equal(DataType.Long,  TypeMapper.MapClrType(typeof(IntBackedEnum)));
        Assert.Equal(DataType.Byte,  TypeMapper.MapClrType(typeof(ByteBackedEnum)));
    }

    [Fact]
    public void TypeMapper_UnmappableType_ReturnsNull()
    {
        Assert.Null(TypeMapper.MapClrType(typeof(System.IO.Stream)));
    }

    [Fact]
    public void TypeMapper_PromotesLongText_ToMemo()
    {
        var col = TypeMapper.ColumnFromClrType("Body", typeof(string), maxLength: 1000);
        Assert.NotNull(col);
        Assert.Equal(DataType.Memo, col!.DataType);
    }

    [Fact]
    public void TypeMapper_PromotesLongBinary_ToOle()
    {
        var col = TypeMapper.ColumnFromClrType("Blob", typeof(byte[]), maxLength: 4096);
        Assert.NotNull(col);
        Assert.Equal(DataType.Ole, col!.DataType);
    }

    [Fact]
    public void TypeMapper_TextColumn_RespectsMaxLength()
    {
        var col = TypeMapper.ColumnFromClrType("Name", typeof(string), maxLength: 30);
        Assert.NotNull(col);
        Assert.Equal(DataType.Text, col!.DataType);
        Assert.Equal(60, col.Length);   // 30 chars × 2 bytes UTF-16
    }

    // ── DataTable import ─────────────────────────────────────────────────────

    [Fact]
    public void ImportTable_FromDataTable_RoundTrips()
    {
        var dt = new DataTable("Customers");
        dt.Columns.Add("Id",        typeof(int));
        dt.Columns.Add("Name",      typeof(string)).MaxLength = 50;
        dt.Columns.Add("Joined",    typeof(DateTime));
        dt.Columns.Add("Balance",   typeof(decimal));
        dt.Columns.Add("IsActive",  typeof(bool));
        dt.PrimaryKey = new[] { dt.Columns["Id"]! };

        dt.Rows.Add(1, "Alice", new DateTime(2024, 5, 1), 100.50m, true);
        dt.Rows.Add(2, "Bob",   new DateTime(2024, 6, 1),  -5.00m, false);
        dt.Rows.Add(3, "Carol", new DateTime(2024, 7, 1),   0.00m, true);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        var t   = reopen.GetTable("Customers");
        var rows = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();

        Assert.Equal(3, rows.Count);
        Assert.Equal("Alice", rows[0]["Name"]);
        Assert.Equal(new DateTime(2024, 6, 1), rows[1]["Joined"]);
        Assert.Equal(0m, rows[2]["Balance"]);
        Assert.Equal(false, rows[1]["IsActive"]);

        // PK was inferred from DataTable.PrimaryKey.
        Assert.Contains(t.Indexes, ix => ix.IsPrimaryKey);
    }

    [Fact]
    public void ImportTable_FromDataTable_NullableValuesPreserved()
    {
        var dt = new DataTable("Things");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Note", typeof(string)).MaxLength = 100;
        dt.Rows.Add(1, DBNull.Value);
        dt.Rows.Add(2, "Hello");

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Things").ReadAllRows()
                          .OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Null(rows[0].GetValueOrDefault("Note"));
        Assert.Equal("Hello", rows[1]["Note"]);
    }

    [Fact]
    public void ImportTable_FromDataTable_ExplicitNameOverridesTableName()
    {
        var dt = new DataTable("ShouldBeIgnored");
        dt.Columns.Add("X", typeof(int));
        dt.Rows.Add(1);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, tableName: "ChosenName");

        using var reopen = Database.Open(_path);
        Assert.Contains("ChosenName", reopen.ListTables());
        Assert.DoesNotContain("ShouldBeIgnored", reopen.ListTables());
    }

    [Fact]
    public void ImportTable_FromDataTable_UnmappableColumn_IsSkipped_AndCallbackFires()
    {
        var dt = new DataTable("Mixed");
        dt.Columns.Add("Id",        typeof(int));
        dt.Columns.Add("Unhandled", typeof(System.IO.Stream));   // not mappable
        dt.Rows.Add(1, null);

        string? droppedName = null;
        Type?   droppedType = null;
        var opts = new ImportOptions
        {
            OnUnmappableColumn = (n, t) => { droppedName = n; droppedType = t; }
        };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: opts);

        Assert.Equal("Unhandled",          droppedName);
        Assert.Equal(typeof(System.IO.Stream), droppedType);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Mixed");
        Assert.Single(t.Columns);
        Assert.Equal("Id", t.Columns[0].Name);
    }

    [Fact]
    public void ImportTable_AllUnmappable_Throws()
    {
        var dt = new DataTable("OnlyBad");
        dt.Columns.Add("S", typeof(System.IO.Stream));

        using var db = Database.Create(_path, JetVersion.Jet4);
        Assert.Throws<InvalidOperationException>(() => db.ImportTable(dt));
    }

    [Fact]
    public void ImportTable_FromDataTable_LongString_PromotedToMemo()
    {
        var dt = new DataTable("Articles");
        dt.Columns.Add("Id",   typeof(int));
        var bodyCol = dt.Columns.Add("Body", typeof(string));
        bodyCol.MaxLength = 5000;
        string bigText = new string('z', 1000);
        dt.Rows.Add(1, bigText);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Articles");
        Assert.Equal(DataType.Memo, t.Columns.Single(c => c.Name == "Body").DataType);
        var row = t.ReadAllRows().Single();
        Assert.Equal(bigText, row["Body"]);
    }

    // ── DataSet import ───────────────────────────────────────────────────────

    [Fact]
    public void ImportTables_FromDataSet_WritesEveryTable()
    {
        var ds = new DataSet("Sales");
        var customers = ds.Tables.Add("Customers");
        customers.Columns.Add("Id",   typeof(int));
        customers.Columns.Add("Name", typeof(string)).MaxLength = 30;
        customers.Rows.Add(1, "Alice");
        customers.Rows.Add(2, "Bob");

        var orders = ds.Tables.Add("Orders");
        orders.Columns.Add("OrderId",    typeof(int));
        orders.Columns.Add("CustomerId", typeof(int));
        orders.Columns.Add("Total",      typeof(decimal));
        orders.Rows.Add(100, 1,  99.99m);
        orders.Rows.Add(101, 1,  19.50m);
        orders.Rows.Add(102, 2,   5.00m);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTables(ds);

        using var reopen = Database.Open(_path);
        var names = reopen.ListTables();
        Assert.Contains("Customers", names);
        Assert.Contains("Orders",    names);
        Assert.Equal(2, reopen.GetTable("Customers").ReadAllRows().Count);
        Assert.Equal(3, reopen.GetTable("Orders"   ).ReadAllRows().Count);
    }

    // ── IEnumerable<T> import (reflection path) ──────────────────────────────

    private sealed class CustomerPoco
    {
        [Key] public int Id { get; set; }
        public string?   Name { get; set; }
        public DateTime  Joined { get; set; }
        public decimal   Balance { get; set; }
        public bool      IsActive { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_RoundTrips()
    {
        var rows = new[]
        {
            new CustomerPoco { Id = 1, Name = "Ann", Joined = new DateTime(2024,1,1), Balance = 10m,  IsActive = true  },
            new CustomerPoco { Id = 2, Name = "Bo",  Joined = new DateTime(2024,2,1), Balance = -5m,  IsActive = false },
            new CustomerPoco { Id = 3, Name = "Cy",  Joined = new DateTime(2024,3,1), Balance =  0m,  IsActive = true  },
        };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("CustomerPoco");
        var saved = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(3, saved.Count);
        Assert.Equal("Ann", saved[0]["Name"]);
        Assert.Equal(-5m,   saved[1]["Balance"]);
        // [Key] picked up Id as PK.
        Assert.Contains(t.Indexes, ix => ix.IsPrimaryKey);
    }

    private sealed class AttributedPoco
    {
        [Column("CustomerKey")]
        [Key]
        public int Id { get; set; }

        [MaxLength(20)]
        public string? Code { get; set; }

        [NotMapped]
        public string?  IgnoreMe { get; set; }

        public Stream? AlsoIgnore { get; set; }   // unmappable type
    }

    [Fact]
    public void ImportTable_FromCollection_RespectsAttributes()
    {
        var rows = new[]
        {
            new AttributedPoco { Id = 1, Code = "AAA", IgnoreMe = "skip", AlsoIgnore = null },
        };

        int dropped = 0;
        var opts = new ImportOptions { OnUnmappableColumn = (_, _) => dropped++ };
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows, options: opts);

        Assert.Equal(1, dropped);   // only AlsoIgnore — IgnoreMe is [NotMapped] and never reaches the mapper

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("AttributedPoco");
        var cols = t.Columns.Select(c => c.Name).ToArray();
        Assert.Contains("CustomerKey", cols);    // renamed via [Column]
        Assert.Contains("Code",        cols);
        Assert.DoesNotContain("Id",        cols);
        Assert.DoesNotContain("IgnoreMe",  cols);
        Assert.DoesNotContain("AlsoIgnore",cols);

        var codeCol = t.Columns.Single(c => c.Name == "Code");
        Assert.Equal(DataType.Text, codeCol.DataType);
        Assert.Equal(40, codeCol.Length);   // MaxLength=20 → 40 bytes UTF-16
    }

    private sealed class NullableValuesPoco
    {
        public int      Id { get; set; }
        public int?     OptInt { get; set; }
        public DateTime? OptDate { get; set; }
        public string?  OptText { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_NullableValues_RoundTrip()
    {
        var rows = new[]
        {
            new NullableValuesPoco { Id = 1, OptInt = 42,   OptDate = new DateTime(2024,1,1), OptText = "hi" },
            new NullableValuesPoco { Id = 2, OptInt = null, OptDate = null,                   OptText = null },
        };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("NullableValuesPoco");
        var saved = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(42,  saved[0]["OptInt"]);
        Assert.Null(saved[1].GetValueOrDefault("OptInt"));
        Assert.Null(saved[1].GetValueOrDefault("OptDate"));
        Assert.Null(saved[1].GetValueOrDefault("OptText"));
    }

    private sealed class EnumPoco
    {
        public int             Id   { get; set; }
        public IntBackedEnum   Kind { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_EnumStoredAsUnderlyingType()
    {
        var rows = new[]
        {
            new EnumPoco { Id = 1, Kind = IntBackedEnum.Beta },
            new EnumPoco { Id = 2, Kind = IntBackedEnum.Alpha },
        };
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("EnumPoco");
        Assert.Equal(DataType.Long, t.Columns.Single(c => c.Name == "Kind").DataType);
        var saved = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal((int)IntBackedEnum.Beta,  saved[0]["Kind"]);
        Assert.Equal((int)IntBackedEnum.Alpha, saved[1]["Kind"]);
    }

    private sealed class DateTimeOffsetPoco
    {
        public int            Id { get; set; }
        public DateTimeOffset When { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_DateTimeOffset_NormalizedToUtcDateTime()
    {
        var ts = new DateTimeOffset(2024, 7, 4, 12, 0, 0, TimeSpan.FromHours(-5));
        var rows = new[] { new DateTimeOffsetPoco { Id = 1, When = ts } };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable("DateTimeOffsetPoco").ReadAllRows().Single();
        Assert.Equal(ts.UtcDateTime, saved["When"]);
    }

    [Fact]
    public void ImportTable_FromCollection_EmptyEnumerable_CreatesEmptyTable()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(Array.Empty<CustomerPoco>());

        using var reopen = Database.Open(_path);
        Assert.Empty(reopen.GetTable("CustomerPoco").ReadAllRows());
    }

    // ── Static factories ─────────────────────────────────────────────────────

    [Fact]
    public void CreateFromDataTable_StaticShortcut_BuildsAndPopulatesFile()
    {
        var dt = new DataTable("Items");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string)).MaxLength = 20;
        dt.Rows.Add(1, "X");
        dt.Rows.Add(2, "Y");

        using (var db = DatabaseImporter.CreateFromDataTable(_path, dt))
        {
            Assert.Equal(2, db.GetTable("Items").ReadAllRows().Count);
        }
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void CreateFromCollection_StaticShortcut_BuildsAndPopulatesFile()
    {
        var rows = Enumerable.Range(1, 4).Select(i => new CustomerPoco
        {
            Id = i, Name = $"P{i}", Joined = new DateTime(2024, 1, i), Balance = i, IsActive = i % 2 == 0
        });

        using (var db = DatabaseImporter.CreateFromCollection(_path, rows))
        {
            Assert.Equal(4, db.GetTable("CustomerPoco").ReadAllRows().Count);
        }
        Assert.True(File.Exists(_path));
    }

    [Fact]
    public void CreateFromDataSet_StaticShortcut_BuildsAndPopulatesFile()
    {
        var ds = new DataSet();
        var dt = ds.Tables.Add("T1");
        dt.Columns.Add("A", typeof(int));
        dt.Rows.Add(1);
        dt.Rows.Add(2);

        using (var db = DatabaseImporter.CreateFromDataSet(_path, ds))
        {
            Assert.Equal(2, db.GetTable("T1").ReadAllRows().Count);
        }
        Assert.True(File.Exists(_path));
    }

    // ── Fallback-to-string for unmappable columns ────────────────────────────

    [Fact]
    public void ImportTable_FromDataTable_Fallback_UriColumn_BecomesMemo()
    {
        var dt = new DataTable("Pages");
        dt.Columns.Add("Id",  typeof(int));
        dt.Columns.Add("Url", typeof(Uri));
        dt.Rows.Add(1, new Uri("https://example.com/a"));
        dt.Rows.Add(2, new Uri("https://example.com/b"));

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: new ImportOptions { FallbackUnmappableToString = true });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Pages");
        Assert.Equal(DataType.Memo, t.Columns.Single(c => c.Name == "Url").DataType);
        var saved = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal("https://example.com/a", saved[0]["Url"]);
        Assert.Equal("https://example.com/b", saved[1]["Url"]);
    }

    [Fact]
    public void ImportTable_FromDataTable_Fallback_NullValueStaysNull()
    {
        var dt = new DataTable("Pages");
        dt.Columns.Add("Id",  typeof(int));
        dt.Columns.Add("Url", typeof(Uri));
        dt.Rows.Add(1, DBNull.Value);
        dt.Rows.Add(2, new Uri("https://x"));

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: new ImportOptions { FallbackUnmappableToString = true });

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Pages").ReadAllRows()
                          .OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Null(rows[0].GetValueOrDefault("Url"));
        Assert.Equal("https://x/", rows[1]["Url"]);   // Uri.ToString() canonicalises
    }

    [Fact]
    public void ImportTable_FromDataTable_Fallback_StillFiresOnUnmappableCallback()
    {
        var dt = new DataTable("Pages");
        dt.Columns.Add("Id",  typeof(int));
        dt.Columns.Add("Url", typeof(Uri));
        dt.Rows.Add(1, new Uri("https://x"));

        int callbackCount = 0;
        var opts = new ImportOptions
        {
            FallbackUnmappableToString = true,
            OnUnmappableColumn = (_, _) => callbackCount++,
        };
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: opts);

        Assert.Equal(1, callbackCount);
        // Even though callback fired, the column was kept (as Memo).
        using var reopen = Database.Open(_path);
        Assert.Equal(2, reopen.GetTable("Pages").Columns.Count);
    }

    private sealed class CustomThing
    {
        public int Tag { get; init; }
        public override string ToString() => $"thing#{Tag}";
    }

    private sealed class WithCustomPropertyPoco
    {
        public int         Id     { get; set; }
        public CustomThing? Thing { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_Fallback_CustomTypeUsesToString()
    {
        var rows = new[]
        {
            new WithCustomPropertyPoco { Id = 1, Thing = new CustomThing { Tag = 10 } },
            new WithCustomPropertyPoco { Id = 2, Thing = new CustomThing { Tag = 20 } },
            new WithCustomPropertyPoco { Id = 3, Thing = null },
        };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows, options: new ImportOptions { FallbackUnmappableToString = true });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("WithCustomPropertyPoco");
        Assert.Equal(DataType.Memo, t.Columns.Single(c => c.Name == "Thing").DataType);

        var saved = t.ReadAllRows().OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal("thing#10", saved[0]["Thing"]);
        Assert.Equal("thing#20", saved[1]["Thing"]);
        Assert.Null(saved[2].GetValueOrDefault("Thing"));
    }

    [Fact]
    public void ImportTable_FromDataTable_Fallback_OnlyUnmappableGetsPromoted()
    {
        // Native types stay native; only the Uri column flips to Memo.
        var dt = new DataTable("Mixed");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string)).MaxLength = 50;
        dt.Columns.Add("Url",  typeof(Uri));
        dt.Rows.Add(1, "A", new Uri("https://a"));

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: new ImportOptions { FallbackUnmappableToString = true });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("Mixed");
        Assert.Equal(DataType.Long, t.Columns.Single(c => c.Name == "Id").DataType);
        Assert.Equal(DataType.Text, t.Columns.Single(c => c.Name == "Name").DataType);
        Assert.Equal(DataType.Memo, t.Columns.Single(c => c.Name == "Url").DataType);
    }

    // ── CoerceForJet (direct unit tests, not through Insert) ─────────────────

    [Fact]
    public void Coerce_NullAndDBNull_BecomeNull()
    {
        Assert.Null(TypeMapper.CoerceForJet(null,         DataType.Long));
        Assert.Null(TypeMapper.CoerceForJet(DBNull.Value, DataType.Text));
    }

    [Fact]
    public void Coerce_Widens_IntToLong_AndNarrows_LongToInt()
    {
        Assert.Equal((short)42, TypeMapper.CoerceForJet(42,       DataType.Int));
        Assert.Equal(42,        TypeMapper.CoerceForJet(42L,      DataType.Long));
        Assert.Equal(42m,       TypeMapper.CoerceForJet(42,       DataType.Money));
    }

    [Fact]
    public void Coerce_DateTimeOffset_BecomesUtcDateTime()
    {
        var dto = new DateTimeOffset(2024, 7, 4, 12, 0, 0, TimeSpan.FromHours(5));
        var asDt = (DateTime)TypeMapper.CoerceForJet(dto, DataType.ShortDateTime)!;
        Assert.Equal(dto.UtcDateTime, asDt);
    }

    [Fact]
    public void Coerce_BinaryNonBytes_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => TypeMapper.CoerceForJet("not bytes", DataType.Binary));
    }

    // ── TypeMapper: wider primitive coverage ─────────────────────────────────

    [Theory]
    [InlineData(typeof(sbyte),    DataType.Byte)]   // signed 8-bit folded into unsigned Byte
    [InlineData(typeof(ushort),   DataType.Int)]
    [InlineData(typeof(uint),     DataType.Long)]
    [InlineData(typeof(ulong),    DataType.Long)]
    [InlineData(typeof(char),     DataType.Text)]
    public void TypeMapper_Maps_LessCommonPrimitives(Type clr, DataType expected)
    {
        Assert.Equal(expected, TypeMapper.MapClrType(clr));
    }

    [Fact]
    public void TypeMapper_LongTruncatesTo32Bit_OnImport()
    {
        // Confirms the documented asymmetry: long → Long (32-bit) → int on read.
        var dt = new DataTable("L");
        dt.Columns.Add("Id", typeof(int));
        dt.Columns.Add("V",  typeof(long));
        dt.Rows.Add(1, 123456789L);
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        Assert.Equal(123456789, reopen.GetTable("L").ReadAllRows().Single()["V"]);
    }

    // ── PK / AutoNumber attribute paths ──────────────────────────────────────

    [Fact]
    public void ImportTable_FromDataTable_ExplicitPrimaryKey_OverridesInferredPK()
    {
        var dt = new DataTable("T");
        dt.Columns.Add("A", typeof(int));
        dt.Columns.Add("B", typeof(int));
        dt.PrimaryKey = new[] { dt.Columns["A"]! };
        dt.Rows.Add(1, 100);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, primaryKey: "B");

        using var reopen = Database.Open(_path);
        var pk = reopen.GetTable("T").Indexes.Single(i => i.IsPrimaryKey);
        Assert.Equal("B", pk.Columns[0].Column.Name);
    }

    [Fact]
    public void ImportTable_FromDataTable_MultiColumnPK_IsIgnored()
    {
        var dt = new DataTable("T");
        dt.Columns.Add("A", typeof(int));
        dt.Columns.Add("B", typeof(int));
        dt.PrimaryKey = new[] { dt.Columns["A"]!, dt.Columns["B"]! };
        dt.Rows.Add(1, 2);

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        // No single-column PK could be inferred from a composite key, so none is created.
        Assert.DoesNotContain(reopen.GetTable("T").Indexes, i => i.IsPrimaryKey);
    }

    [Fact]
    public void ImportTable_FromDataTable_DeletedRows_AreSkipped()
    {
        var dt = new DataTable("T");
        dt.Columns.Add("Id", typeof(int));
        dt.Rows.Add(1);
        dt.Rows.Add(2);
        dt.Rows.Add(3);
        dt.AcceptChanges();
        dt.Rows[1].Delete();   // mark row "Id=2" Deleted

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt);

        using var reopen = Database.Open(_path);
        var ids = reopen.GetTable("T").ReadAllRows()
                          .Select(r => Convert.ToInt32(r["Id"])).OrderBy(i => i).ToList();
        Assert.Equal(new[] { 1, 3 }, ids);
    }

    private sealed class IdentityPoco
    {
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_DatabaseGeneratedIdentity_MarksAutoNumber()
    {
        var rows = new[] { new IdentityPoco { Id = 1, Name = "A" } };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var idCol = reopen.GetTable("IdentityPoco").Columns.Single(c => c.Name == "Id");
        Assert.True(idCol.IsAutoNumber);
    }

    [Fact]
    public void ImportTable_FromCollection_ExplicitPK_OverridesKeyAttribute()
    {
        var rows = new[] { new CustomerPoco
        {
            Id = 1, Name = "A", Joined = new DateTime(2024,1,1), Balance = 10m, IsActive = true,
        }};
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows, primaryKey: "Name");

        using var reopen = Database.Open(_path);
        var pk = reopen.GetTable("CustomerPoco").Indexes.Single(i => i.IsPrimaryKey);
        Assert.Equal("Name", pk.Columns[0].Column.Name);
    }

    [Fact]
    public void ImportTables_EmptyDataSet_CreatesNoTables()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTables(new DataSet());

        using var reopen = Database.Open(_path);
        Assert.Empty(reopen.ListTables());
    }

    private sealed class ReadOnlyPropertyPoco
    {
        public int     Id   { get; set; }
        public string  Name { get; } = "computed";   // no setter
    }

    [Fact]
    public void ImportTable_FromCollection_ReadOnlyProperty_IsStillIncluded()
    {
        // Get-only auto-properties have CanRead=true; the importer reads them.
        // (Get-only is fine for write — we only read via reflection.)
        var rows = new[] { new ReadOnlyPropertyPoco { Id = 1 } };
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("ReadOnlyPropertyPoco");
        var saved = t.ReadAllRows().Single();
        Assert.Equal("computed", saved["Name"]);
    }

    // ── AppendIfExists — importing into a pre-existing table ─────────────────

    [Fact]
    public void ImportTable_FromDataTable_TableExists_DefaultThrows()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("Customers", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        });

        var dt = new DataTable("Customers");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string));
        dt.Rows.Add(1, "Alice");

        var ex = Assert.Throws<InvalidOperationException>(() => db.ImportTable(dt));
        Assert.Contains("AppendIfExists", ex.Message);
    }

    [Fact]
    public void ImportTable_FromDataTable_AppendIfExists_AddsRowsToExistingTable()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            var existing = db.CreateTable("Customers", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            }, primaryKey: "Id");
            existing.Insert(new Row { ["Id"] = 1, ["Name"] = "Seed" });
        }

        var dt = new DataTable("Customers");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string));
        dt.Rows.Add(2, "Bob");
        dt.Rows.Add(3, "Carol");

        using (var db = Database.Open(_path))
            db.ImportTable(dt, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var rows = reopen.GetTable("Customers").ReadAllRows()
                         .OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("Seed",  rows[0]["Name"]);
        Assert.Equal("Bob",   rows[1]["Name"]);
        Assert.Equal("Carol", rows[2]["Name"]);
    }

    [Fact]
    public void ImportTable_FromDataTable_AppendIfExists_ExtraSourceColumnIsIgnored()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            });

        var dt = new DataTable("T");
        dt.Columns.Add("Id",        typeof(int));
        dt.Columns.Add("Name",      typeof(string));
        dt.Columns.Add("Unrelated", typeof(string));   // not in target schema → dropped
        dt.Rows.Add(1, "Alice", "ignored value");

        using (var db = Database.Open(_path))
            db.ImportTable(dt, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").ReadAllRows().Single();
        Assert.Equal("Alice", row["Name"]);
        // Target only has Id + Name; "Unrelated" never made it in.
        Assert.DoesNotContain(reopen.GetTable("T").Columns, c => c.Name == "Unrelated");
    }

    [Fact]
    public void ImportTable_FromDataTable_AppendIfExists_MissingSourceColumnStaysNull()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
                new ColumnBuilder("Note", DataType.Memo).Build(),
            });

        // Source only supplies Id + Name — Note should land as null in the new row.
        var dt = new DataTable("T");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string));
        dt.Rows.Add(1, "Alice");

        using (var db = Database.Open(_path))
            db.ImportTable(dt, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").ReadAllRows().Single();
        Assert.Equal("Alice", row["Name"]);
        Assert.Null(row.GetValueOrDefault("Note"));
    }

    [Fact]
    public void ImportTable_FromDataTable_AppendIfExists_TableMissing_CreatesAsUsual()
    {
        // When AppendIfExists is true but the table doesn't exist yet, fall
        // back to the create-new path so the option is a strict superset.
        var dt = new DataTable("Brand-New");
        dt.Columns.Add("Id",   typeof(int));
        dt.Columns.Add("Name", typeof(string));
        dt.Rows.Add(1, "Hello");

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(dt, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        Assert.Contains("Brand-New", reopen.ListTables());
        Assert.Single(reopen.GetTable("Brand-New").ReadAllRows());
    }

    [Fact]
    public void ImportTable_FromCollection_TableExists_DefaultThrows()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.CreateTable("CustomerPoco", new[]
        {
            new ColumnBuilder("Id",       DataType.Long).Build(),
            new ColumnBuilder("Name",     DataType.Text).MaxLength(50).Build(),
            new ColumnBuilder("Joined",   DataType.ShortDateTime).Build(),
            new ColumnBuilder("Balance",  DataType.Money).Build(),
            new ColumnBuilder("IsActive", DataType.Boolean).Build(),
        });

        var ex = Assert.Throws<InvalidOperationException>(
            () => db.ImportTable(new[] { new CustomerPoco { Id = 1, Name = "X" } }));
        Assert.Contains("AppendIfExists", ex.Message);
    }

    [Fact]
    public void ImportTable_FromCollection_AppendIfExists_AddsRows()
    {
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("CustomerPoco", new[]
            {
                new ColumnBuilder("Id",       DataType.Long).Build(),
                new ColumnBuilder("Name",     DataType.Text).MaxLength(50).Build(),
                new ColumnBuilder("Joined",   DataType.ShortDateTime).Build(),
                new ColumnBuilder("Balance",  DataType.Money).Build(),
                new ColumnBuilder("IsActive", DataType.Boolean).Build(),
            }, primaryKey: "Id");
        }

        var rows = new[]
        {
            new CustomerPoco { Id = 1, Name = "A", Joined = new DateTime(2024,1,1), Balance = 10m, IsActive = true  },
            new CustomerPoco { Id = 2, Name = "B", Joined = new DateTime(2024,2,1), Balance = -5m, IsActive = false },
        };

        using (var db = Database.Open(_path))
            db.ImportTable(rows, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable("CustomerPoco").ReadAllRows()
                         .OrderBy(r => Convert.ToInt32(r["Id"])).ToList();
        Assert.Equal(2, saved.Count);
        Assert.Equal("A", saved[0]["Name"]);
        Assert.Equal(-5m, saved[1]["Balance"]);
    }

    [Fact]
    public void ImportTable_AppendIfExists_CoercesValuesToTargetColumnType()
    {
        // Target column is Text (string). Source supplies an int. The append
        // path should coerce via TypeMapper.CoerceForJet, ending up with the
        // int's string representation in the Text column.
        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.CreateTable("T", new[]
            {
                new ColumnBuilder("Id",    DataType.Long).Build(),
                new ColumnBuilder("Label", DataType.Text).MaxLength(20).Build(),
            });

        var dt = new DataTable("T");
        dt.Columns.Add("Id",    typeof(int));
        dt.Columns.Add("Label", typeof(int));      // source int → target Text
        dt.Rows.Add(1, 42);

        using (var db = Database.Open(_path))
            db.ImportTable(dt, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var row = reopen.GetTable("T").ReadAllRows().Single();
        Assert.Equal(1,    row["Id"]);
        Assert.Equal("42", row["Label"]);
    }

    [Fact]
    public void ImportTable_AppendIfExists_FromCollection_PreservesExistingSchema()
    {
        // Confirm the existing schema (PK, column lengths, etc.) is left
        // untouched after an append-only operation.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("CustomerPoco", new[]
            {
                new ColumnBuilder("Id",       DataType.Long).Build(),
                new ColumnBuilder("Name",     DataType.Text).MaxLength(50).Build(),
                new ColumnBuilder("Joined",   DataType.ShortDateTime).Build(),
                new ColumnBuilder("Balance",  DataType.Money).Build(),
                new ColumnBuilder("IsActive", DataType.Boolean).Build(),
            }, primaryKey: "Id");
        }

        using (var db = Database.Open(_path))
            db.ImportTable(
                new[] { new CustomerPoco { Id = 1, Name = "appended", Joined = DateTime.Today, Balance = 0m, IsActive = true } },
                options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var t = reopen.GetTable("CustomerPoco");
        // PK still there.
        Assert.Contains(t.Indexes, ix => ix.IsPrimaryKey);
        // Schema intact (still 5 columns, Text length still 50).
        Assert.Equal(5,   t.Columns.Count);
        Assert.Equal(100, t.Columns.Single(c => c.Name == "Name").Length);   // 50 chars × 2 bytes
    }

    [Fact]
    public void ImportTables_DataSet_AppendIfExists_MixedNewAndExisting()
    {
        // One existing table + one brand-new table in the same DataSet.
        // With AppendIfExists, the existing one should be appended and the
        // missing one should be created — net result is both populated.
        using (var db = Database.Create(_path, JetVersion.Jet4))
        {
            db.CreateTable("Existing", new[]
            {
                new ColumnBuilder("Id", DataType.Long).Build()
            }).Insert(new Row { ["Id"] = 999 });
        }

        var ds = new DataSet();
        var existing = ds.Tables.Add("Existing");
        existing.Columns.Add("Id", typeof(int));
        existing.Rows.Add(1);
        existing.Rows.Add(2);

        var brandNew = ds.Tables.Add("BrandNew");
        brandNew.Columns.Add("X", typeof(int));
        brandNew.Rows.Add(42);

        using (var db = Database.Open(_path))
            db.ImportTables(ds, options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        Assert.Equal(3, reopen.GetTable("Existing").ReadAllRows().Count);   // 999 + 1 + 2
        Assert.Single(reopen.GetTable("BrandNew").ReadAllRows());
    }

    // ── [Table] attribute support ────────────────────────────────────────────

    [Table("Customers")]
    private sealed class TableAttributedPoco
    {
        public int Id { get; set; }
        public string? Name { get; set; }
    }

    [Fact]
    public void ImportTable_FromCollection_RespectsTableAttribute()
    {
        var rows = new[] { new TableAttributedPoco { Id = 1, Name = "Ada" } };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows);

        using var reopen = Database.Open(_path);
        // [Table("Customers")] renames the target table away from the CLR type name.
        Assert.Contains("Customers", reopen.ListTables());
        Assert.DoesNotContain("TableAttributedPoco", reopen.ListTables());
    }

    [Fact]
    public void ImportTable_FromCollection_ExplicitTableNameOverridesTableAttribute()
    {
        var rows = new[] { new TableAttributedPoco { Id = 1, Name = "Ada" } };

        using (var db = Database.Create(_path, JetVersion.Jet4))
            db.ImportTable(rows, tableName: "People");

        using var reopen = Database.Open(_path);
        // The explicit parameter wins over [Table].
        Assert.Contains("People",    reopen.ListTables());
        Assert.DoesNotContain("Customers",          reopen.ListTables());
        Assert.DoesNotContain("TableAttributedPoco", reopen.ListTables());
    }

    [Fact]
    public void ImportTable_FromCollection_TableAttribute_AppendIfExists_FindsExistingByAttributeName()
    {
        // Seed an "Customers" table the long way, then import rows of a POCO
        // whose [Table] attribute points at that same name. With AppendIfExists,
        // the importer should resolve the target by attribute name and append.
        using (var seed = Database.Create(_path, JetVersion.Jet4))
        {
            seed.CreateTable("Customers", new[]
            {
                new ColumnBuilder("Id",   DataType.Long).Build(),
                new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
            }, primaryKey: "Id");
        }

        using (var db = Database.Open(_path))
            db.ImportTable(new[] { new TableAttributedPoco { Id = 1, Name = "Ada" } },
                options: new ImportOptions { AppendIfExists = true });

        using var reopen = Database.Open(_path);
        var saved = reopen.GetTable("Customers").ReadAllRows();
        Assert.Single(saved);
        Assert.Equal("Ada", saved[0]["Name"]);
    }

    // ── Helper types ─────────────────────────────────────────────────────────

    public enum IntBackedEnum  { Alpha = 1, Beta = 2 }
    public enum ByteBackedEnum : byte { Off = 0, On = 1 }
}
