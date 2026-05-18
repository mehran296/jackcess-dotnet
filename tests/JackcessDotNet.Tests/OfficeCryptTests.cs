using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// End-to-end tests for the Office Agile-Encryption codec (Office 2010+ .accdb).
/// Uses real password-protected fixtures from the upstream jackcess-encrypt repo
/// at <c>D:/Projects/jackcess-encrypt/src/test/resources/data/</c>. If that
/// directory isn't present, the tests skip (via <see cref="Skip.If"/> behaviour
/// expressed as early returns) rather than fail — running on a fresh machine
/// shouldn't need a manual clone of the reference repo just to run unit tests.
/// </summary>
public sealed class OfficeCryptTests
{
    private const string FixtureRoot =
        @"D:/Projects/jackcess-encrypt/src/test/resources/data";

    private static bool FixtureExists(string name)
        => File.Exists(Path.Combine(FixtureRoot, name));

    [Fact]
    public void Open_Db2013Enc_WithCorrectPassword_Succeeds()
    {
        string path = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(path)) return;   // skip when fixtures absent

        // db2013-enc.accdb seeds a "Customers" table (not "Table1" — that's the
        // Standard 2007 fixture). Match the upstream Java test names so we can
        // cross-check assertions against the reference port.
        using var db = Database.Open(path, "1234");
        var tables = db.ListTables();
        Assert.Contains("Customers", tables);

        // Upstream seeds 7 rows with columns ID + Field1; we just confirm we
        // can decrypt + parse a TDEF + read rows without crashing.
        var t = db.GetTable("Customers");
        var rows = t.ReadAllRows();
        Assert.NotNull(rows);
        Assert.NotEmpty(rows);
    }

    [Fact]
    public void Open_Db2013Enc_WithWrongPassword_Throws()
    {
        string path = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(path)) return;

        Assert.Throws<UnauthorizedAccessException>(
            () => Database.Open(path, "definitely-not-1234"));
    }

    [Fact]
    public void Open_Db2013Enc_WithoutPassword_GivesClearError()
    {
        string path = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(path)) return;

        // Opening an encrypted .accdb without a password should produce a clear
        // "appears encrypted" error rather than a cryptic page-parse crash.
        var ex = Assert.ThrowsAny<Exception>(() => Database.Open(path));
        Assert.Contains("encrypted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Open_UnencryptedAccdb_WithBogusPassword_StillOpens()
    {
        // Password should be silently ignored when the encoding-key slot is zero
        // (i.e. the file isn't encrypted). Use any unencrypted .accdb from the
        // main jackcess corpus.
        const string corpus = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        string? path = Directory.Exists(corpus)
            ? Directory.EnumerateFiles(corpus, "*.accdb").FirstOrDefault()
            : null;
        if (path is null) return;

        using var db = Database.Open(path, "this-password-is-ignored");
        Assert.NotEmpty(db.ListTables(includeSystem: true));
    }

    [Theory]
    [InlineData("db2007-enc.accdb")]
    [InlineData("db2007-oldenc.accdb")]
    public void Open_StandardEncryption2007_WithCorrectPassword_Succeeds(string fixtureName)
    {
        string path = Path.Combine(FixtureRoot, fixtureName);
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "Test123");
        Assert.Contains("Table1", db.ListTables());
        var rows = db.GetTable("Table1").ReadAllRows();
        Assert.NotNull(rows);   // tolerant of empty/seeded content; just must not crash
    }

    [Theory]
    [InlineData("db2007-enc.accdb")]
    [InlineData("db2007-oldenc.accdb")]
    public void Open_StandardEncryption2007_WithWrongPassword_Throws(string fixtureName)
    {
        string path = Path.Combine(FixtureRoot, fixtureName);
        if (!File.Exists(path)) return;

        Assert.Throws<UnauthorizedAccessException>(() => Database.Open(path, "definitely-wrong"));
    }

    // ── Write / round-trip tests ─────────────────────────────────────────────

    [Fact]
    public void Write_Then_Reopen_Standard2007_PreservesAddedRow()
        => DoWriteThenReopen("db2007-enc.accdb", "Test123");

    [Fact]
    public void Write_Then_Reopen_Agile2013_PreservesAddedRow()
        => DoWriteThenReopen("db2013-enc.accdb", "1234");

    /// <summary>
    /// Shared write-roundtrip: open the fixture, insert a row built from the
    /// real column list (skipping auto-num PKs), reopen, verify the row count
    /// increased. Column-name-agnostic so it works on whatever schema the
    /// upstream fixture happens to ship with.
    /// </summary>
    private void DoWriteThenReopen(string fixtureName, string password)
    {
        string src = Path.Combine(FixtureRoot, fixtureName);
        if (!File.Exists(src)) return;

        string tmp = Path.Combine(Path.GetTempPath(), $"rw_{Guid.NewGuid():N}.accdb");
        File.Copy(src, tmp);
        try
        {
            string tableName;
            int initialCount;
            using (var db = Database.Open(tmp, password))
            {
                tableName = db.ListTables().FirstOrDefault()
                    ?? throw new InvalidOperationException("Fixture has no user tables.");
                var t = db.GetTable(tableName);
                initialCount = t.ReadAllRows().Count;

                var row = new Row();
                foreach (var col in t.Columns)
                {
                    if (col.IsAutoNumber) continue;
                    row[col.Name] = DummyValueFor(col);
                }
                t.Insert(row);
            }

            using var reopen = Database.Open(tmp, password);
            int newCount = reopen.GetTable(tableName).ReadAllRows().Count;
            Assert.Equal(initialCount + 1, newCount);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    private static object? DummyValueFor(Column col) => col.DataType switch
    {
        DataType.Long          => 1,
        DataType.Int           => (short)1,
        DataType.Byte          => (byte)1,
        DataType.Double        => 1.0,
        DataType.Float         => 1.0f,
        DataType.Money         => 1m,
        DataType.Numeric       => 1m,
        DataType.Boolean       => true,
        DataType.Text          => "x",
        DataType.Memo          => "x",
        DataType.Binary        => new byte[] { 1 },
        DataType.Guid          => Guid.NewGuid(),
        DataType.ShortDateTime => new DateTime(2024, 1, 1),
        _                      => null,
    };

    [Fact]
    public void Encode_RoundTrips_ThroughTheCodec_Standard()
    {
        // Synthetic page round-trip: encode then decode must yield the original
        // bytes for any page number > 0. Independent of any on-disk file.
        string src = Path.Combine(FixtureRoot, "db2007-enc.accdb");
        if (!File.Exists(src)) return;

        byte[] header = ReadHeaderPage(src);
        var codec = OfficeCryptCodecHandler.FromDbHeader(header, "Test123");
        Assert.NotNull(codec);

        byte[] plaintext = new byte[4096];
        new Random(123).NextBytes(plaintext);
        byte[] encoded = codec!.EncodePage(plaintext, pageNumber: 42);
        byte[] decoded = codec.DecodePage(encoded, pageNumber: 42);

        Assert.Equal(plaintext, decoded);
        Assert.NotEqual(plaintext, encoded);
    }

    [Fact]
    public void Encode_RoundTrips_ThroughTheCodec_Agile()
    {
        string src = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(src)) return;

        byte[] header = ReadHeaderPage(src);
        var codec = OfficeCryptCodecHandler.FromDbHeader(header, "1234");
        Assert.NotNull(codec);

        byte[] plaintext = new byte[4096];
        new Random(456).NextBytes(plaintext);
        byte[] encoded = codec!.EncodePage(plaintext, pageNumber: 17);
        byte[] decoded = codec.DecodePage(encoded, pageNumber: 17);

        Assert.Equal(plaintext, decoded);
        Assert.NotEqual(plaintext, encoded);
    }

    [Fact]
    public void Encode_Page0_IsPassedThrough()
    {
        string src = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(src)) return;

        byte[] header = ReadHeaderPage(src);
        var codec = OfficeCryptCodecHandler.FromDbHeader(header, "1234");
        Assert.NotNull(codec);

        byte[] page0 = new byte[4096];
        new Random(7).NextBytes(page0);
        Assert.Equal(page0, codec!.EncodePage(page0, pageNumber: 0));
        Assert.Equal(page0, codec.DecodePage(page0, pageNumber: 0));
    }

    private static byte[] ReadHeaderPage(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        byte[] page0 = new byte[4096];
        Buffer.BlockCopy(all, 0, page0, 0, Math.Min(all.Length, page0.Length));
        return page0;
    }

    // ── Non-Standard AES variant (db-nonstandard.accdb fixture) ─────────────

    [Fact]
    public void Open_NonStandardAesAccdb_WithCorrectPassword_Succeeds()
    {
        // db-nonstandard.accdb has FAES_FLAG clear yet uses AES; falls back
        // to the "compat mode 0" path (AES with 0 hash iterations).
        string path = Path.Combine(FixtureRoot, "db-nonstandard.accdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "password");
        Assert.NotEmpty(db.ListTables());
    }

    [Fact]
    public void Open_NonStandardAesAccdb_WithWrongPassword_Throws()
    {
        string path = Path.Combine(FixtureRoot, "db-nonstandard.accdb");
        if (!File.Exists(path)) return;

        Assert.Throws<UnauthorizedAccessException>(() => Database.Open(path, "wrong"));
    }

    // ── Scheme reporting per encryption variant ─────────────────────────────

    [Theory]
    [InlineData("db2013-enc.accdb",    "1234",    "Agile")]
    [InlineData("db2007-enc.accdb",    "Test123", "Agile")]      // Office 2007 file but actually Agile
    [InlineData("db2007-oldenc.accdb", "Test123", "RC4")]        // true RC4 CryptoAPI
    [InlineData("db-nonstandard.accdb","password","Non-Standard")]
    public void Codec_Scheme_NamesActualEncryptionFlavor(
        string fixtureName, string password, string expectedSchemeFragment)
    {
        string path = Path.Combine(FixtureRoot, fixtureName);
        if (!File.Exists(path)) return;

        byte[] header = ReadHeaderPage(path);
        var codec = OfficeCryptCodecHandler.FromDbHeader(header, password);
        Assert.NotNull(codec);
        Assert.Contains(expectedSchemeFragment, codec!.Scheme,
                        StringComparison.OrdinalIgnoreCase);
    }

    // ── Seeded-data round-trip (not just "doesn't crash") ───────────────────

    [Fact]
    public void Read_Db2013Enc_Customers_HasSevenSeededRows_WithExpectedField1Values()
    {
        // From doCheckOffice2013Db in upstream Java: Customers table seeded with
        // ID 1-7 / Field1 values { "Test","Test2","a",null,"c","d","f" }.
        string path = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "1234");
        var rows = db.GetTable("Customers").ReadAllRows()
                     .OrderBy(r => Convert.ToInt32(r["ID"])).ToList();
        Assert.Equal(7, rows.Count);

        var expected = new[] { "Test", "Test2", "a", null, "c", "d", "f" };
        for (int i = 0; i < expected.Length; i++)
        {
            object? actual = rows[i].GetValueOrDefault("Field1");
            Assert.Equal(expected[i], actual);
        }
    }

    [Fact]
    public void Read_Db2007Enc_Table1_HasOneSeededRow_FooField1()
    {
        // From doCheckOfficeDb in upstream Java: Table1 seeded with ID=1, Field1="foo".
        string path = Path.Combine(FixtureRoot, "db2007-enc.accdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "Test123");
        var rows = db.GetTable("Table1").ReadAllRows();
        var row  = rows.Single();
        Assert.Equal(1,     Convert.ToInt32(row["ID"]));
        Assert.Equal("foo", row["Field1"]);
    }

    [Fact]
    public void Read_Db2007OldEnc_Table1_HasOneSeededRow_RC4Path()
    {
        // Same seeded shape as db2007-enc, but this file is RC4 CryptoAPI rather
        // than Agile — confirms the RC4 codec actually decrypts data, not just
        // verifies passwords.
        string path = Path.Combine(FixtureRoot, "db2007-oldenc.accdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "Test123");
        var row = db.GetTable("Table1").ReadAllRows().Single();
        Assert.Equal(1,     Convert.ToInt32(row["ID"]));
        Assert.Equal("foo", row["Field1"]);
    }

    [Fact]
    public void Read_NonStandardAccdb_TableOne_HasIdColumn()
    {
        // db-nonstandard.accdb's table is "Table_One" (not "Table1") with an "ID"
        // column — matches upstream testNonStandardProvider.
        string path = Path.Combine(FixtureRoot, "db-nonstandard.accdb");
        if (!File.Exists(path)) return;

        using var db = Database.Open(path, "password");
        var t = db.GetTable("Table_One");
        Assert.Contains(t.Columns, c => c.Name.Equals("ID", StringComparison.OrdinalIgnoreCase));
    }

    // ── Bulk write under encryption ─────────────────────────────────────────

    [Fact]
    public void Write_ManyRows_UnderAgileEncryption_AllSurviveReopen()
    {
        string src = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(src)) return;

        string tmp = Path.Combine(Path.GetTempPath(), $"bulk_{Guid.NewGuid():N}.accdb");
        File.Copy(src, tmp);
        try
        {
            int initialCount;
            using (var db = Database.Open(tmp, "1234"))
            {
                var t = db.GetTable("Customers");
                initialCount = t.ReadAllRows().Count;
                for (int i = 0; i < 50; i++)
                    t.Insert(new Row { ["ID"] = 100 + i, ["Field1"] = $"bulk-{i}" });
            }

            using var reopen = Database.Open(tmp, "1234");
            var rows = reopen.GetTable("Customers").ReadAllRows();
            Assert.Equal(initialCount + 50, rows.Count);
            Assert.Equal(50, rows.Count(r => (r.GetValueOrDefault("Field1") as string)?.StartsWith("bulk-") == true));
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    // ── Header-mask demask edge case ────────────────────────────────────────

    [Fact]
    public void FromDbHeader_EncodingKeyEqualsHeaderMask_TreatedAsUnencrypted()
    {
        // Synthetic page: bytes at 0x3E are exactly the header mask, so after
        // demasking the encoding key is all zero → file is not encrypted.
        // Real-world example: linkeeTest.accdb from the main jackcess corpus.
        byte[] page = new byte[4096];
        page[0x3E] = 0xFB; page[0x3F] = 0x8A;     // these are HEADER_MASK[38..42]
        page[0x40] = 0xBC; page[0x41] = 0x4E;
        // EncryptionInfo struct left all-zero, so the version field reads 0.0.

        var codec = OfficeCryptCodecHandler.FromDbHeader(page, "any-password");
        Assert.Null(codec);
    }

    [Fact]
    public void Codec_AdvertisesActiveSchemeOnAccessor()
    {
        // Read the header and ask the codec which scheme it picked. The factory
        // is the public-surface entry point; tests are the only consumer of the
        // Scheme property right now, but it's useful for debugging in the wild.
        string path = Path.Combine(FixtureRoot, "db2013-enc.accdb");
        if (!File.Exists(path)) return;

        byte[] header = File.ReadAllBytes(path);
        // Trim to first page (the header lives in page 0; default page size = 4 KiB).
        byte[] page0 = new byte[4096];
        Buffer.BlockCopy(header, 0, page0, 0, Math.Min(header.Length, page0.Length));
        var codec = OfficeCryptCodecHandler.FromDbHeader(page0, "1234");
        Assert.NotNull(codec);
        Assert.Contains("Agile", codec!.Scheme);
    }
}
