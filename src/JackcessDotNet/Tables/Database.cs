using System.IO;

namespace JackcessDotNet;

/// <summary>
/// Entry point for creating and opening Microsoft Access (.mdb) database files.
///
/// Usage:
/// <code>
///   using var db = Database.Create("MyFile.mdb", JetVersion.Jet4);
///   var table = db.CreateTable("Customers", new[]
///   {
///       new ColumnBuilder("Id",   DataType.Long ).AutoNumber().Build(),
///       new ColumnBuilder("Name", DataType.Text ).MaxLength(100).Build(),
///   });
///   table.Insert(new Row { ["Id"] = 1, ["Name"] = "Alice" });
/// </code>
/// </summary>
public sealed class Database : IDisposable
{
    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;
    private readonly SystemCatalog _catalog;
    private IReadOnlyList<Relationship>? _relsCache;

    private Database(PageFile file)
    {
        _file      = file;
        _allocator = new PageAllocator(file);
        _catalog   = new SystemCatalog(file);
    }

    /// <summary>
    /// When true, <see cref="Table.Insert"/> validates that every FK column
    /// value maps to an existing row in the parent table per the relationships
    /// declared in <c>MSysRelationships</c>. Default <c>false</c> for backward
    /// compatibility — Access historically left FK validation to the
    /// application layer, not the engine.
    /// <para>
    /// Restrict-only — no cascade behaviour is implemented. Null FK values are
    /// allowed (matches SQL semantics). Composite FKs are checked column-by-column.
    /// </para>
    /// </summary>
    public bool EnforceForeignKeys { get; set; }

    // ── Factory methods ───────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new blank .mdb file at <paramref name="path"/> and returns an
    /// open <see cref="Database"/> ready for use.
    /// </summary>
    public static Database Create(string path, JetVersion version)
    {
        var format = version == JetVersion.Jet3 ? JetFormat.Jet3 : JetFormat.Jet4;
        var file   = new PageFile(path, format, FileMode.Create);
        var db     = new Database(file);

        DatabaseHeader.Create(format).WriteTo(file);

        return db;
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file using <paramref name="password"/> to
    /// decrypt password-protected pages.
    ///
    /// For Jet4 (.mdb), the RC4-based MSISAM codec is used — see
    /// <see cref="JetCryptCodecHandler"/> for caveats around verification.
    /// For ACE 12+ (.accdb) the Office Crypto codec is used: <b>Agile
    /// Encryption</b> (Office 2010+, MS-OFFCRYPTO §2.3.4.10–13) and <b>ECMA
    /// Standard Encryption</b> (Office 2007, §2.3.4.5–9). RC4 CryptoAPI and
    /// extensible-provider variants still throw with a clear error — see
    /// <see cref="OfficeCryptCodecHandler"/>.
    /// </summary>
    public static Database Open(string path, string password)
    {
        if (string.IsNullOrEmpty(password)) return Open(path);

        // Sniff version + signature first.
        Span<byte> head = stackalloc byte[0x15];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(head);
            if (read < 0x15)
                throw new InvalidDataException($"File '{path}' is too short to be an Access database.");
        }
        if (!StartsWithSignature(head, "Standard Jet DB")
         && !StartsWithSignature(head, "Standard ACE DB"))
            throw new InvalidDataException(
                $"File '{path}' is not a recognised Access database.");

        byte versionCode = head[0x14];
        return versionCode switch
        {
            0x00 => OpenWithJetCrypt   (path, JetVersion.Jet3, password),
            0x01 => OpenWithJetCrypt   (path, JetVersion.Jet4, password),
            0x02 => OpenWithOfficeCrypt(path, JetVersion.Jet12, password),
            0x03 => OpenWithOfficeCrypt(path, JetVersion.Jet14, password),
            0x04 => OpenWithOfficeCrypt(path, JetVersion.Jet16, password),
            _    => OpenWithOfficeCrypt(path, JetVersion.Jet17, password),
        };
    }

    /// <summary>
    /// Opens an .accdb that may be password-protected via Office Agile Encryption.
    /// If the encoding-key slot in the header is zero, the file is unencrypted
    /// and the password is silently ignored. Otherwise the codec is installed
    /// (which itself verifies the password) and a decrypt-on-read database is
    /// returned.
    /// </summary>
    private static Database OpenWithOfficeCrypt(string path, JetVersion version, string password)
    {
        JetFormat format = version switch
        {
            JetVersion.Jet12 => JetFormat.Jet12,
            JetVersion.Jet14 => JetFormat.Jet14,
            JetVersion.Jet16 => JetFormat.Jet16,
            _                => JetFormat.Jet17,
        };

        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            byte[] header = file.ReadPage(0);
            var codec = OfficeCryptCodecHandler.FromDbHeader(header, password);
            if (codec is not null)
                file.Codec = codec;

            // Whether we installed a codec or not, the system catalog page now
            // has to look like a TDEF. If it doesn't, we got the password wrong
            // (or the file uses an unsupported codec we silently accepted).
            byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
            if (catalogPage[0] != JetFormat.PageTypeTableDef || catalogPage[1] != 0x01)
                throw new UnauthorizedAccessException(
                    $"Failed to decrypt '{path}' — wrong password, or the file uses an " +
                    "encryption scheme not yet supported by this codec.");
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Opens a Jet 3/4 (.mdb) file. The password is used only when page 2
    /// doesn't decrypt as a TableDef in cleartext — in that case the file is
    /// taken to be encrypted and <see cref="JetCryptCodecHandler"/> is installed
    /// with the password as the master-key seed. Unencrypted files ignore the
    /// password (no codec is installed).
    /// </summary>
    private static Database OpenWithJetCrypt(string path, JetVersion version, string password)
    {
        JetFormat format = version == JetVersion.Jet3 ? JetFormat.Jet3 : JetFormat.Jet4;
        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            // Try cleartext first — most .mdb files in the wild aren't encrypted.
            byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
            bool looksLikeTdef =
                catalogPage[0] == JetFormat.PageTypeTableDef && catalogPage[1] == 0x01;
            if (looksLikeTdef)
                return new Database(file);   // Unencrypted — ignore the password.

            // Page 2 doesn't decode as a TableDef in cleartext → assume encryption,
            // install the codec, retry.
            byte[] dbHeader = file.ReadPage(0);
            var codec = JetCryptCodecHandler.FromDbHeader(dbHeader, password);
            if (codec is null)
                throw new UnauthorizedAccessException(
                    $"'{path}' appears encrypted but no password was supplied " +
                    "(or its verifier block is empty).");
            file.Codec = codec;

            byte[] retry = file.ReadPage(JetFormat.PageSystemCatalog);
            if (retry[0] != JetFormat.PageTypeTableDef || retry[1] != 0x01)
                throw new UnauthorizedAccessException(
                    $"Failed to decrypt '{path}' — wrong password or unsupported encryption scheme.");
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Opens an existing .mdb / .accdb file.
    /// </summary>
    public static Database Open(string path, JetVersion version)
    {
        JetFormat format = version switch
        {
            JetVersion.Jet3  => JetFormat.Jet3,
            JetVersion.Jet4  => JetFormat.Jet4,
            JetVersion.Jet12 => JetFormat.Jet12,
            JetVersion.Jet14 => JetFormat.Jet14,
            JetVersion.Jet16 => JetFormat.Jet16,
            JetVersion.Jet17 => JetFormat.Jet17,
            _                => throw new NotSupportedException($"Unsupported JetVersion: {version}")
        };
        var file = new PageFile(path, format, FileMode.Open);
        try
        {
            ThrowIfEncrypted(file, path);
        }
        catch
        {
            file.Dispose();
            throw;
        }
        return new Database(file);
    }

    /// <summary>
    /// Sniffs whether the database appears encrypted by inspecting the system
    /// catalog page (page 2). For unencrypted files this page starts with
    /// 0x02 0x01 (TableDef header); encrypted files have those bytes XOR'd
    /// against a key-derived stream and will start with anything else.
    ///
    /// We raise a clear error rather than letting downstream parsers fail with
    /// cryptic messages. Once an <see cref="ICodecHandler"/> implementation lands
    /// (RC4/Office Crypto), this check should install the codec instead of throwing.
    /// </summary>
    private static void ThrowIfEncrypted(PageFile file, string path)
    {
        // File may be smaller than 3 pages (corrupt / not yet populated) — skip
        // the check rather than throw a spurious "encrypted" error.
        if (file.PageCount < 3) return;
        byte[] catalogPage = file.ReadPage(JetFormat.PageSystemCatalog);
        if (catalogPage[0] != JetFormat.PageTypeTableDef || catalogPage[1] != 0x01)
            throw new NotSupportedException(
                $"File '{path}' appears to be encrypted or corrupt — the system catalog " +
                $"page (page 2) does not start with the TableDef header (0x02 0x01). " +
                $"If it is password-protected, open it via Database.Open(path, password).");
    }

    /// <summary>
    /// Opens an existing Access database file, auto-detecting the Jet format
    /// version from the byte at file offset 0x14:
    ///   0x00 = Jet3 (Access 97)
    ///   0x01 = Jet4 (Access 2000-2003)
    ///   0x02+ = .accdb formats (not yet supported)
    /// </summary>
    public static Database Open(string path)
    {
        Span<byte> head = stackalloc byte[0x15];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            int read = fs.Read(head);
            if (read < 0x15)
                throw new InvalidDataException($"File '{path}' is too short to be an Access database.");
        }

        // Validate the database-header signature at offset 0x04:
        //   "Standard Jet DB" — Jet 3/4 (.mdb)
        //   "Standard ACE DB" — ACE 12+ (.accdb)
        // Any other content means the file isn't a recognisable Access database;
        // failing here gives a much clearer error than letting the page parser
        // crash on garbage bytes downstream.
        if (!StartsWithSignature(head, "Standard Jet DB")
         && !StartsWithSignature(head, "Standard ACE DB"))
            throw new InvalidDataException(
                $"File '{path}' is not a recognised Access database (missing 'Standard Jet/ACE DB' signature).");

        byte versionCode = head[0x14];
        return versionCode switch
        {
            0x00 => Open(path, JetVersion.Jet3),
            0x01 => Open(path, JetVersion.Jet4),
            0x02 => Open(path, JetVersion.Jet12),
            0x03 => Open(path, JetVersion.Jet14),
            0x04 => Open(path, JetVersion.Jet16),
            // Newer ACE generations (0x05+) re-use the V17 layout we know about;
            // fall through to Jet17 rather than refusing to open.
            _    => Open(path, JetVersion.Jet17)
        };
    }

    // ── Foreign-key validation ───────────────────────────────────────────────

    /// <summary>
    /// Called by <see cref="Table.Insert"/> when <see cref="EnforceForeignKeys"/>
    /// is set. Walks the relationships in MSysRelationships, finds the ones
    /// where <paramref name="childTable"/> is the dependent side, and confirms
    /// every non-null FK value in <paramref name="row"/> has a matching parent
    /// row. Throws on the first violation; relationships are checked in
    /// declaration order.
    /// </summary>
    internal void ValidateForeignKeysForInsert(Table childTable, Row row)
    {
        if (!EnforceForeignKeys) return;
        var rels = _relsCache ??= GetRelationships();
        if (rels.Count == 0) return;

        foreach (var rel in rels)
        {
            if (!string.Equals(rel.ToTable, childTable.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var (fromCol, toCol) in rel.ColumnPairs)
            {
                if (!row.TryGetValue(toCol, out var value) || value is null)
                    continue;   // SQL semantics: NULL FK is allowed
                if (!ParentRowExists(rel.FromTable, fromCol, value))
                    throw new InvalidOperationException(
                        $"Foreign-key violation in relationship '{rel.Name}': " +
                        $"'{childTable.Name}.{toCol}' = {value} has no matching " +
                        $"'{rel.FromTable}.{fromCol}'.");
            }
        }
    }

    /// <summary>
    /// Look up whether <paramref name="parentTableName"/> has any row whose
    /// <paramref name="parentCol"/> equals <paramref name="value"/>. Uses the
    /// primary-key index when the parent column happens to be the PK; otherwise
    /// falls back to a linear scan.
    /// </summary>
    private bool ParentRowExists(string parentTableName, string parentCol, object value)
    {
        Table parent;
        try { parent = GetTable(parentTableName); }
        catch (InvalidOperationException) { return false; }   // table gone → constraint can't hold

        // Fast path: parent column is the PK and there's exactly one PK column.
        var pkIx = parent.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pkIx is { Columns.Count: 1 } &&
            string.Equals(pkIx.Columns[0].Column.Name, parentCol, StringComparison.OrdinalIgnoreCase))
        {
            return parent.NewIndexCursor().FindRowByPrimaryKey(value) is not null;
        }

        // Slow path: linear scan. Used for non-PK relationships (rare but valid).
        foreach (var r in parent.NewCursor())
        {
            if (r.TryGetValue(parentCol, out var v) && v is not null && v.Equals(value))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the first bytes of <paramref name="buf"/> (starting at
    /// offset 0x04 — Jet's signature position) match the ASCII string
    /// <paramref name="signature"/>.
    /// </summary>
    private static bool StartsWithSignature(ReadOnlySpan<byte> buf, string signature)
    {
        const int offset = 0x04;
        if (buf.Length < offset + signature.Length) return false;
        for (int i = 0; i < signature.Length; i++)
            if (buf[offset + i] != (byte)signature[i]) return false;
        return true;
    }

    /// <summary>
    /// Returns the names of all user tables in the database (skips MSys* system tables).
    /// </summary>
    public IReadOnlyList<string> ListTables(bool includeSystem = false)
        => _catalog.GetTableNames(includeSystem);

    /// <summary>
    /// Reads MSysRelationships and returns the schema's foreign-key / join
    /// relationships, grouping the per-column-pair rows back into one entry
    /// per named relationship. Returns an empty list when MSysRelationships
    /// is absent or empty.
    /// </summary>
    public IReadOnlyList<Relationship> GetRelationships()
    {
        try
        {
            var t = GetTable("MSysRelationships");
            var rows = t.NewCursor().ToList();
            return BuildRelationships(rows);
        }
        catch (InvalidOperationException)
        {
            // Database has no MSysRelationships (rare — empty Access files include it,
            // but defensive in case of corrupt or trimmed files).
            return Array.Empty<Relationship>();
        }
    }

    private static IReadOnlyList<Relationship> BuildRelationships(IReadOnlyList<Row> rows)
    {
        // MSysRelationships layout per Jackcess:
        //   szRelationship          - relationship name (groups rows together)
        //   szReferencedObject      - FROM table (parent)
        //   szReferencedColumn      - FROM column
        //   szObject                - TO table (child)
        //   szColumn                - TO column
        //   icolumn                 - 0-based pair index within the relationship
        //   ccolumn                 - total pair count
        //   grbit                   - flags (one-to-one, cascade, joins)
        var byName = new Dictionary<string, (string from, string to, int flags,
                                             List<(int idx, string fromCol, string toCol)> pairs)>(
                                             StringComparer.OrdinalIgnoreCase);

        foreach (var r in rows)
        {
            string name = r.TryGetValue("szRelationship", out var n) ? n?.ToString() ?? "" : "";
            if (string.IsNullOrEmpty(name)) continue;
            string from = r.TryGetValue("szReferencedObject", out var f) ? f?.ToString() ?? "" : "";
            string to   = r.TryGetValue("szObject",           out var t) ? t?.ToString() ?? "" : "";
            string fc   = r.TryGetValue("szReferencedColumn", out var fcv) ? fcv?.ToString() ?? "" : "";
            string tc   = r.TryGetValue("szColumn",           out var tcv) ? tcv?.ToString() ?? "" : "";
            int idx     = r.TryGetValue("icolumn", out var iv) && iv is not null ? Convert.ToInt32(iv) : 0;
            int flags   = r.TryGetValue("grbit",   out var gv) && gv is not null ? Convert.ToInt32(gv) : 0;

            if (!byName.TryGetValue(name, out var entry))
            {
                entry = (from, to, flags, new List<(int, string, string)>());
                byName[name] = entry;
            }
            entry.pairs.Add((idx, fc, tc));
        }

        var result = new List<Relationship>(byName.Count);
        foreach (var (name, e) in byName)
        {
            var pairs = e.pairs.OrderBy(p => p.idx)
                               .Select(p => (p.fromCol, p.toCol))
                               .ToList();
            result.Add(new Relationship(name, e.from, e.to, e.flags, pairs));
        }
        return result;
    }

    // ── Table operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Creates a new user table, writes its TDEF and usage-map pages, registers
    /// it in MSysObjects, and returns a <see cref="Table"/> ready for inserts.
    /// </summary>
    /// <param name="name">Table name (must be unique in the database).</param>
    /// <param name="columns">Column definitions (use <see cref="ColumnBuilder"/>).</param>
    /// <param name="primaryKey">
    ///   Optional name of the primary-key column.  A structurally valid but
    ///   empty index leaf page is created; B-tree maintenance on insert is not
    ///   yet implemented.
    /// </param>
    public Table CreateTable(string name, IReadOnlyList<Column> columns, string? primaryKey = null)
        => CreateTableCore(name, columns, primaryKey is null ? null : new[] { primaryKey });

    /// <summary>
    /// Composite-PK overload. Creates a table with a multi-column primary key
    /// (up to 10 columns — Jet's hard limit). Pass the column names in the
    /// order they should appear in the index.
    /// </summary>
    public Table CreateTable(string name, IReadOnlyList<Column> columns,
                             IReadOnlyList<string> primaryKeyColumns)
    {
        if (primaryKeyColumns is null || primaryKeyColumns.Count == 0)
            throw new ArgumentException(
                "primaryKeyColumns must contain at least one column.", nameof(primaryKeyColumns));
        return CreateTableCore(name, columns, primaryKeyColumns);
    }

    private Table CreateTableCore(string name, IReadOnlyList<Column> columns,
                                  IReadOnlyList<string>? pkColumns)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));
        if (columns is null || columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));

        var format = _file.Format;

        // 1. Allocate the TDEF page (content written in step 3).
        int tdefPage = _allocator.AllocatePage();

        // 2. Allocate the usage-map page (owned pages + free-space maps).
        int umapPage = _allocator.AllocateUmapPage();

        // 3. Allocate a LVAL usage-map page for each Memo/OLE column (must happen before
        //    Serialize so the page numbers can be embedded in the TDEF).
        var lvalUmaps = new System.Collections.Generic.Dictionary<string, int>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var col in columns)
            if (col.DataType.IsLongValue())
                lvalUmaps[col.Name] = _allocator.AllocateUmapPage();

        // 4. Build the TableDefinition (column numbers + var-table indices will be assigned
        //    by Serialize, but PK info must be set BEFORE Serialize so the index column
        //    block can be persisted with the correct column number and root page).
        var tableDef = new TableDefinition(name, columns)
        {
            TdefPageNumber = tdefPage,
            UmapPageNumber = umapPage,
            OwnedPagesRow  = 0,
            FreeSpaceRow   = 1,
        };
        foreach (var kvp in lvalUmaps)
            tableDef.LvalColumnUmapPages[kvp.Key] = kvp.Value;

        // Assign column numbers up front so the index block can reference the correct
        // column. Serialize will re-assign these in the same order — same outcome.
        for (short i = 0; i < columns.Count; i++)
            columns[i].ColumnNumber = i;

        // 5. Pre-allocate the PK index leaf so its page number is known at Serialize time.
        if (pkColumns is { Count: > 0 })
        {
            var idxWriter = new IndexWriter(_file, _allocator);
            tableDef.PrimaryKeyIndexPage = idxWriter.CreatePrimaryKeyIndex(tableDef, pkColumns);
            if (pkColumns.Count == 1)
                tableDef.PrimaryKeyColumnName = pkColumns[0];
            else
                tableDef.PrimaryKeyColumnNames = pkColumns;
        }

        // 6. Now serialise — the index column block + slot + name will be included.
        byte[] tdefBytes = tableDef.Serialize(format);
        _file.WritePage(tdefPage, tdefBytes);

        // 7. Register the table in MSysObjects.
        _catalog.InsertTableEntry(name, tdefPage);

        return new Table(_file, _allocator, tableDef, owningDb: this);
    }

    /// <summary>
    /// Opens an existing user table by name (scans MSysObjects).
    /// </summary>
    public Table GetTable(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Table name must not be empty.", nameof(name));

        int tdefPageNum = _catalog.FindTableTdefPage(name);
        if (tdefPageNum < 0)
            throw new InvalidOperationException($"Table '{name}' was not found in this database.");

        var format   = _file.Format;
        var tdefPage = _file.ReadPage(tdefPageNum);
        var info     = TdefReader.Read(tdefPage, format);

        var tableDef = new TableDefinition(name, info.Columns)
        {
            TdefPageNumber = tdefPageNum,
            UmapPageNumber = info.OwnedPagesUmapPage,
            OwnedPagesRow  = info.OwnedPagesUmapRow,
            FreeSpaceRow   = info.FreeSpaceUmapRow,
            Indexes        = info.Indexes,
        };

        foreach (var kvp in info.LvalColumnUmapPages)
            tableDef.LvalColumnUmapPages[kvp.Key] = kvp.Value;

        // Reattach in-memory PK config from the on-disk metadata so that
        // IndexCursor.FindRowByPrimaryKey works across open/close boundaries.
        var pkIndex = info.Indexes.FirstOrDefault(ix => ix.IsPrimaryKey);
        if (pkIndex is not null && pkIndex.Columns.Count > 0)
        {
            tableDef.PrimaryKeyIndexPage = pkIndex.RootPageNumber;
            if (pkIndex.Columns.Count == 1)
                tableDef.PrimaryKeyColumnName = pkIndex.Columns[0].Column.Name;
            else
                tableDef.PrimaryKeyColumnNames =
                    pkIndex.Columns.Select(ic => ic.Column.Name).ToArray();
        }

        return new Table(_file, _allocator, tableDef, owningDb: this);
    }

    public void Dispose() => _file.Dispose();
}
