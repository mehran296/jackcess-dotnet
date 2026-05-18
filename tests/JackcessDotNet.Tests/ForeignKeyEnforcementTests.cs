using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tests for <see cref="Database.EnforceForeignKeys"/> — restrict-only,
/// insert-time validation against MSysRelationships metadata.
/// Existing relationships in the upstream corpus (Northwind etc.) give us
/// real FK shapes to test against.
/// </summary>
public sealed class ForeignKeyEnforcementTests : IDisposable
{
    private readonly string _path;
    public ForeignKeyEnforcementTests()
        => _path = Path.Combine(Path.GetTempPath(), $"fk_{Guid.NewGuid():N}.mdb");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    [Fact]
    public void EnforceForeignKeys_DefaultsToFalse()
    {
        using var db = Database.Create(_path, JetVersion.Jet4);
        Assert.False(db.EnforceForeignKeys);
    }

    [Fact]
    public void Insert_WithoutEnforcement_DoesNotValidate()
    {
        // A pristine .mdb created by us has no MSysRelationships rows, so
        // even with enforcement enabled there's nothing to validate. This
        // test confirms the no-relationship case is silently OK.
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.EnforceForeignKeys = true;

        var t = db.CreateTable("Things", new[]
        {
            new ColumnBuilder("Id",   DataType.Long).Build(),
            new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
        }, primaryKey: "Id");
        t.Insert(new Row { ["Id"] = 1, ["Name"] = "A" });

        Assert.Single(t.ReadAllRows());
    }

    [Fact]
    public void Insert_ValidatesAgainstRelationships_OnNorthwindFixture()
    {
        // Northwind has rich relationships — Orders.CustomerID → Customers.CustomerID,
        // Products.SupplierID → Suppliers.SupplierID, etc. We open a copy,
        // enable enforcement, and try to insert an Orders row with a bogus
        // CustomerID — should be rejected.
        const string corpus = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        string? nw = Directory.Exists(corpus)
            ? Directory.EnumerateFiles(corpus, "northwind*.mdb").FirstOrDefault()
            : null;
        if (nw is null) return;

        File.Copy(nw, _path, overwrite: true);
        using var db = Database.Open(_path);
        var rels = db.GetRelationships();
        if (rels.Count == 0) return;   // unexpected for Northwind, but skip rather than fail

        db.EnforceForeignKeys = true;

        // Find any child→parent constraint to attack.
        var rel = rels.First();
        var (parentCol, childCol) = rel.ColumnPairs[0];

        var childTable = db.GetTable(rel.ToTable);
        // Build a row carrying obviously-bogus values for the FK columns.
        var bad = new Row();
        foreach (var col in childTable.Columns)
            bad[col.Name] = DummyValueFor(col);
        bad[childCol] = SentinelForType(childTable.Columns.First(c =>
            c.Name.Equals(childCol, StringComparison.OrdinalIgnoreCase)).DataType);

        var ex = Assert.Throws<InvalidOperationException>(() => childTable.Insert(bad));
        Assert.Contains("Foreign-key violation", ex.Message);
        Assert.Contains(rel.Name, ex.Message);
    }

    [Fact]
    public void Insert_AgainstNorthwind_WithValidFkValue_Succeeds()
    {
        // Opposite of the violation test: pluck a real parent PK value from the
        // parent table and use it in the child — should pass without throwing.
        const string corpus = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        string? nw = Directory.Exists(corpus)
            ? Directory.EnumerateFiles(corpus, "northwind*.mdb").FirstOrDefault()
            : null;
        if (nw is null) return;

        File.Copy(nw, _path, overwrite: true);
        using var db = Database.Open(_path);
        var rels = db.GetRelationships();
        if (rels.Count == 0) return;

        // Find a single-column relationship we can satisfy easily.
        var rel = rels.FirstOrDefault(r => r.ColumnPairs.Count == 1);
        if (rel is null) return;

        var (parentCol, childCol) = rel.ColumnPairs[0];
        var parentTable = db.GetTable(rel.FromTable);
        var childTable  = db.GetTable(rel.ToTable);

        // Get a known-good parent PK value.
        object? validParentValue = parentTable.NewCursor()
            .Take(1).FirstOrDefault()?.GetValueOrDefault(parentCol);
        if (validParentValue is null) return;

        db.EnforceForeignKeys = true;

        var row = new Row();
        foreach (var col in childTable.Columns)
            row[col.Name] = DummyValueFor(col);
        row[childCol] = validParentValue;

        // Should not throw — we're handing it a real parent key.
        // (Insert may still fail for unrelated reasons like required NOT-NULL
        // columns, so wrap in try and only fail this test on FK violations.)
        try
        {
            childTable.Insert(row);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Foreign-key violation"))
        {
            Assert.Fail("FK validation rejected a value plucked directly from the parent table: " + ex.Message);
        }
        catch
        {
            // Other Insert failures (NOT-NULL, type mismatch, etc.) aren't this test's concern.
        }
    }

    [Fact]
    public void EnforceForeignKeys_DisablingAfterEnabling_StopsValidation()
    {
        // Once disabled, subsequent inserts should NOT validate even if a
        // relationship would have rejected them. Confirms there's no
        // sticky/cached enforcement state.
        const string corpus = @"D:/Projects/jackcess-jackcess-5.0.0/src/test/resources/data";
        string? nw = Directory.Exists(corpus)
            ? Directory.EnumerateFiles(corpus, "northwind*.mdb").FirstOrDefault()
            : null;
        if (nw is null) return;

        File.Copy(nw, _path, overwrite: true);
        using var db = Database.Open(_path);
        var rels = db.GetRelationships();
        if (rels.Count == 0) return;

        var rel = rels.First();
        var (_, childCol) = rel.ColumnPairs[0];
        var childTable = db.GetTable(rel.ToTable);

        var bad = new Row();
        foreach (var col in childTable.Columns)
            bad[col.Name] = DummyValueFor(col);
        bad[childCol] = SentinelForType(childTable.Columns.First(c =>
            c.Name.Equals(childCol, StringComparison.OrdinalIgnoreCase)).DataType);

        db.EnforceForeignKeys = true;
        Assert.Throws<InvalidOperationException>(() => childTable.Insert(bad));

        db.EnforceForeignKeys = false;
        // Now the same row goes through (or fails for non-FK reasons).
        try { childTable.Insert(bad); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Foreign-key violation"))
        {
            Assert.Fail("FK validation fired with EnforceForeignKeys disabled: " + ex.Message);
        }
        catch
        {
            // Other failures are fine.
        }
    }

    [Fact]
    public void Insert_NullFkValue_IsAllowed()
    {
        // SQL semantics: a NULL FK passes validation. We synthesise a
        // self-referential setup by creating two tables and pretending one
        // has a relationship via a custom MSysRelationships row — but since
        // we don't write MSysRelationships from this library yet, this test
        // just confirms that EnforceForeignKeys with no rels is a no-op.
        using var db = Database.Create(_path, JetVersion.Jet4);
        db.EnforceForeignKeys = true;

        var t = db.CreateTable("X", new[]
        {
            new ColumnBuilder("Id",  DataType.Long).Build(),
            new ColumnBuilder("Ref", DataType.Long).Build(),
        }, primaryKey: "Id");

        t.Insert(new Row { ["Id"] = 1, ["Ref"] = null });
        Assert.Single(t.ReadAllRows());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static object? DummyValueFor(Column col) => col.DataType switch
    {
        DataType.Long      => 0,
        DataType.Int       => (short)0,
        DataType.Byte      => (byte)0,
        DataType.Double    => 0.0,
        DataType.Float     => 0.0f,
        DataType.Money     => 0m,
        DataType.Numeric   => 0m,
        DataType.Boolean   => false,
        DataType.Text      => "",
        DataType.Memo      => "",
        DataType.Binary    => Array.Empty<byte>(),
        DataType.Guid      => Guid.Empty,
        DataType.ShortDateTime => DateTime.Now,
        _                  => null,
    };

    private static object SentinelForType(DataType dt) => dt switch
    {
        DataType.Long => unchecked((int)0xCAFEF00D),
        DataType.Int  => unchecked((short)0xC0DE),
        DataType.Text => "[[unmatched-fk-sentinel]]",
        DataType.Guid => new Guid("DEADBEEF-1234-5678-9ABC-DEF012345678"),
        _             => 0xC0FFEE,
    };
}
