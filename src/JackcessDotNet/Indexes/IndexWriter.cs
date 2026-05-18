using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>
/// Writes B-tree index pages in real Access on-disk format and maintains the tree
/// shape across inserts.
///
/// Tree shape produced today:
///   • Root starts as a leaf page (type 0x04) for small indexes that fit on one page.
///   • On the first leaf overflow, root is promoted to a node page (type 0x03)
///     pointing at the original leaf (now left half) plus the new sibling leaf
///     (right half).
///   • Subsequent overflows split a leaf, update the corresponding node entry's
///     key to the new left-max, and add a new node entry for the right sibling.
///   • The depth is currently capped at 2 (root node + leaves). When the root
///     node itself fills, we throw — a 3-level tree needs recursive node splits,
///     deferred to a future slice.
///
/// Within a single page:
///   • Leaves carry entries sorted ascending by key bytes (so Access can binary-
///     search them).
///   • Node entries are also sorted; each entry's key is the LARGEST key in its
///     subtree (matches the descent convention <see cref="IndexReader"/> uses).
///
/// On a root change (initial leaf-to-node promotion), the index's
/// <c>rootPage</c> field inside the TDEF's index column block is patched so the
/// next <see cref="Database.Open"/> sees the new root.
/// </summary>
public sealed class IndexWriter
{
    private const int  NoPage         = unchecked((int)0xFFFFFFFF);
    private const byte AscStartFlag   = 0x7F;
    private const int  LeafTrailerLen = 4;   // 3-byte BE page + 1-byte row
    private const int  NodeTrailerLen = 8;   // leaf trailer + 4-byte BE sub-page

    private readonly PageFile      _file;
    private readonly PageAllocator _allocator;

    public IndexWriter(PageFile file, PageAllocator allocator)
    {
        _file      = file      ?? throw new ArgumentNullException(nameof(file));
        _allocator = allocator ?? throw new ArgumentNullException(nameof(allocator));
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Allocates a fresh, empty leaf page and returns its page number.</summary>
    public int CreateEmptyLeafPage()
    {
        int pn = _allocator.AllocatePage();
        _file.WritePage(pn, BuildEmptyIndexPage(_file.Format, isLeaf: true));
        return pn;
    }

    public int CreatePrimaryKeyIndex(TableDefinition table, string indexName)
        => CreateEmptyLeafPage();

    /// <summary>
    /// Composite-PK companion to <see cref="CreatePrimaryKeyIndex(TableDefinition, string)"/>.
    /// The index layout is identical to a single-column PK — the difference is
    /// only in how key bytes get encoded on insertion.
    /// </summary>
    public int CreatePrimaryKeyIndex(TableDefinition table, IReadOnlyList<string> pkColumns)
        => CreateEmptyLeafPage();

    public int? FindRowByPrimaryKey(TableDefinition table, object primaryKeyValue)
    {
        foreach (int rowPtr in EnumerateRowPointersForKey(table, primaryKeyValue))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Composite-PK variant of <see cref="FindRowByPrimaryKey"/>. Encodes all
    /// values together and searches the tree once for the concatenated key.
    /// </summary>
    public int? FindRowByPrimaryKey(TableDefinition table, IReadOnlyList<object?> values)
    {
        byte[] key = EncodeCompositeKeyBytes(values);
        foreach (int rowPtr in EnumerateRowPointersForKeyBytes(table, key))
            return rowPtr;
        return null;
    }

    /// <summary>
    /// Walks the tree rooted at <see cref="TableDefinition.PrimaryKeyIndexPage"/>
    /// and yields rowPtrs for every entry whose key bytes equal the encoded form
    /// of <paramref name="primaryKeyValue"/>. Visits siblings via the leaf chain
    /// after the first matching leaf so duplicate keys (left by Update/Delete) are
    /// all reported.
    /// </summary>
    public IEnumerable<int> EnumerateRowPointersForKey(TableDefinition table, object primaryKeyValue)
        => EnumerateRowPointersForKeyBytes(table, EncodeKeyBytes(primaryKeyValue));

    /// <summary>
    /// Internal scan over the B-tree for the encoded key bytes. Shared between
    /// the single-column and composite-PK lookup paths.
    /// </summary>
    private IEnumerable<int> EnumerateRowPointersForKeyBytes(TableDefinition table, byte[] search)
    {
        if (table.PrimaryKeyIndexPage == 0) yield break;
        var format = _file.Format;

        // Descend from root to the leaf that should contain the key.
        int curLeaf = DescendToLeaf(table.PrimaryKeyIndexPage, search);
        while (curLeaf > 0 && curLeaf != NoPage)
        {
            byte[] page = _file.ReadPage(curLeaf);
            foreach (var entry in ReadEntries(page, format, isLeaf: true))
            {
                if (entry.KeyBytes.Length != search.Length) continue;
                bool match = true;
                for (int i = 0; i < search.Length; i++)
                    if (entry.KeyBytes[i] != search[i]) { match = false; break; }
                if (match) yield return entry.RowPtr;
            }
            int next = ByteUtil.GetInt(page, format.OffsetNextIndexPage);
            if (next == NoPage) break;
            // Stop scanning siblings once the leaf's first key is past the search key
            // (entries are sorted ascending — no further matches possible).
            curLeaf = next;
            byte[] nextPage = _file.ReadPage(curLeaf);
            var firstEntry = ReadEntries(nextPage, format, isLeaf: true).FirstOrDefault();
            if (firstEntry.KeyBytes is null) break;
            if (CompareBytes(firstEntry.KeyBytes, search) > 0) break;
        }
    }

    /// <summary>
    /// Inserts a new (key, rowPtr) pair into the B-tree, splitting + promoting
    /// when necessary. May change <see cref="TableDefinition.PrimaryKeyIndexPage"/>
    /// if the root splits.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, object primaryKeyValue, int rowPointer)
        => InsertPrimaryKeyBytes(table, EncodeKeyBytes(primaryKeyValue), rowPointer);

    /// <summary>
    /// Composite-PK variant. Encodes the multi-column key (single ascending
    /// flag prefix + per-column bytes) and inserts it into the same B-tree.
    /// </summary>
    public void InsertPrimaryKey(TableDefinition table, IReadOnlyList<object?> values, int rowPointer)
        => InsertPrimaryKeyBytes(table, EncodeCompositeKeyBytes(values), rowPointer);

    private void InsertPrimaryKeyBytes(TableDefinition table, byte[] keyBytes, int rowPointer)
    {
        if (table.PrimaryKeyIndexPage == 0)
            throw new InvalidOperationException(
                "Table has no primary key index page. " +
                "Specify a primary key column name when calling Database.CreateTable.");

        var format = _file.Format;

        int rootPage = table.PrimaryKeyIndexPage;
        byte[] root = _file.ReadPage(rootPage);
        bool rootIsLeaf = root[0] == JetFormat.PageTypeIndexLeaf;

        if (rootIsLeaf)
        {
            // Two-level path: try inserting into the root leaf directly.
            var split = InsertIntoLeaf(rootPage, keyBytes, rowPointer);
            if (split is null) return;

            // Leaf split — promote: create a node above pointing at both halves.
            int newRoot = CreateRootNode(
                leftPage:    rootPage, leftMaxKey:  split.Value.LeftMaxKey,
                rightPage:   split.Value.NewSiblingPage, rightMaxKey: split.Value.RightMaxKey);
            table.PrimaryKeyIndexPage = newRoot;
            PatchTdefRoot(table, newRoot);
            return;
        }

        // Three-level path: root is a node. Descend to the right leaf, insert, and
        // if it splits, update the node entry's key + insert a new entry.
        int leafPage = DescendToLeaf(rootPage, keyBytes);
        var leafSplit = InsertIntoLeaf(leafPage, keyBytes, rowPointer);
        if (leafSplit is null) return;

        // Update node to reflect the leaf split: child's key shrinks to LeftMaxKey,
        // and we add a new entry pointing at the new sibling with RightMaxKey.
        UpdateNodeForLeafSplit(rootPage,
            oldChildPage: leafPage, oldChildNewKey: leafSplit.Value.LeftMaxKey,
            newChildPage: leafSplit.Value.NewSiblingPage, newChildKey: leafSplit.Value.RightMaxKey);
    }

    // ── Internals ────────────────────────────────────────────────────────────

    /// <summary>
    /// Insert a single (keyBytes, rowPtr) into the leaf at <paramref name="leafPage"/>.
    /// Returns null if the entry fit; otherwise the split info to propagate upward.
    /// </summary>
    private LeafSplit? InsertIntoLeaf(int leafPage, byte[] keyBytes, int rowPointer)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(leafPage);
        var entries = ReadEntries(page, format, isLeaf: true);

        var newEntry = BuildLeafEntry(keyBytes, rowPointer);
        InsertSortedLeaf(entries, newEntry);

        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        int totalBytes = entries.Sum(e => e.RawBytes.Length);

        if (totalBytes <= entriesAreaSize && entries.Count <= 3624)
        {
            // Fits — rewrite the whole leaf in place.
            WriteEntries(leafPage, entries, format, isLeaf: true);
            return null;
        }

        // Doesn't fit — split into two leaves.
        int mid = entries.Count / 2;
        var left  = entries.GetRange(0, mid);
        var right = entries.GetRange(mid, entries.Count - mid);

        // Allocate the right sibling and stitch into the next-pointer chain.
        int rightPage = _allocator.AllocatePage();
        byte[] rightPg = BuildEmptyIndexPage(format, isLeaf: true);
        int origNext = ByteUtil.GetInt(page, format.OffsetNextIndexPage);
        ByteUtil.PutInt(rightPg, format.OffsetPrevIndexPage, leafPage);
        ByteUtil.PutInt(rightPg, format.OffsetNextIndexPage, origNext);
        _file.WritePage(rightPage, rightPg);

        ByteUtil.PutInt(page, format.OffsetNextIndexPage, rightPage);
        _file.WritePage(leafPage, page);

        WriteEntries(leafPage,  left,  format, isLeaf: true);
        WriteEntries(rightPage, right, format, isLeaf: true);

        return new LeafSplit(
            LeftMaxKey:     left[^1].KeyBytes,
            RightMaxKey:    right[^1].KeyBytes,
            NewSiblingPage: rightPage);
    }

    private int CreateRootNode(int leftPage, byte[] leftMaxKey, int rightPage, byte[] rightMaxKey)
    {
        var format = _file.Format;
        int newRootPage = _allocator.AllocatePage();
        byte[] node = BuildEmptyIndexPage(format, isLeaf: false);

        var entries = new List<RawEntry>
        {
            BuildNodeEntry(leftMaxKey,  leftPage),
            BuildNodeEntry(rightMaxKey, rightPage),
        };
        _file.WritePage(newRootPage, node);
        WriteEntries(newRootPage, entries, format, isLeaf: false);
        return newRootPage;
    }

    private void UpdateNodeForLeafSplit(
        int nodePage, int oldChildPage, byte[] oldChildNewKey,
        int newChildPage, byte[] newChildKey)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(nodePage);
        var entries = ReadEntries(page, format, isLeaf: false);

        // Find the entry whose subPage == oldChildPage and rebuild it with the new key.
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].SubPage == oldChildPage)
            {
                entries[i] = BuildNodeEntry(oldChildNewKey, oldChildPage);
                break;
            }
        }

        // Insert the new sibling's entry in sorted order.
        var newSibEntry = BuildNodeEntry(newChildKey, newChildPage);
        InsertSortedLeaf(entries, newSibEntry);

        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        int totalBytes = entries.Sum(e => e.RawBytes.Length);
        if (totalBytes > entriesAreaSize || entries.Count > 3624)
            throw new NotSupportedException(
                $"Root node is full ({entries.Count} children). " +
                "Tree depths greater than 2 (root node + leaves) are not yet supported.");

        WriteEntries(nodePage, entries, format, isLeaf: false);
    }

    /// <summary>
    /// Descends from <paramref name="startPage"/> (root) to the leaf whose key
    /// range covers <paramref name="searchKey"/>. Convention: first entry with
    /// <c>entry.key &gt;= searchKey</c> wins; if none, descend to the last entry.
    /// </summary>
    private int DescendToLeaf(int startPage, byte[] searchKey)
    {
        var format = _file.Format;
        int cur = startPage;
        while (true)
        {
            byte[] page = _file.ReadPage(cur);
            if (page[0] == JetFormat.PageTypeIndexLeaf) return cur;
            if (page[0] != JetFormat.PageTypeIndexNode) return cur;

            var entries = ReadEntries(page, format, isLeaf: false);
            int target = -1;
            foreach (var e in entries)
            {
                if (CompareBytes(e.KeyBytes, searchKey) >= 0)
                {
                    target = e.SubPage;
                    break;
                }
            }
            if (target < 0)
                target = entries[^1].SubPage;
            cur = target;
        }
    }

    /// <summary>
    /// Reads all entries from a leaf or node page, decoded into <see cref="RawEntry"/>
    /// records. Entries are returned in their on-disk order (which our writer keeps
    /// sorted ascending). Compressed-prefix is not supported on the write path —
    /// we always emit full-length entries with compressedByteCount = 0.
    /// </summary>
    private static List<RawEntry> ReadEntries(byte[] page, JetFormat format, bool isLeaf)
    {
        var result = new List<RawEntry>();
        int trailerLen   = isLeaf ? LeafTrailerLen : NodeTrailerLen;
        int entryMaskPos = format.OffsetIndexEntryMask;
        int maskLen      = format.SizeIndexEntryMask;
        int entriesPos   = entryMaskPos + maskLen;

        int lastStart = 0;
        for (int i = 0; i < maskLen; i++)
        {
            byte b = page[entryMaskPos + i];
            if (b == 0) continue;
            for (int j = 0; j < 8; j++)
            {
                if ((b & (1 << j)) == 0) continue;
                int endOffset = i * 8 + j;
                int entryLen  = endOffset - lastStart;
                int entryAbs  = entriesPos + lastStart;
                if (entryLen >= trailerLen)
                {
                    int keyLen = entryLen - trailerLen;
                    var key = new byte[keyLen];
                    Array.Copy(page, entryAbs, key, 0, keyLen);

                    int pgBE = (page[entryAbs + keyLen]     << 16)
                             | (page[entryAbs + keyLen + 1] <<  8)
                             |  page[entryAbs + keyLen + 2];
                    int row  = page[entryAbs + keyLen + 3];
                    int subPage = 0;
                    if (!isLeaf)
                    {
                        subPage = (page[entryAbs + keyLen + 4] << 24)
                                | (page[entryAbs + keyLen + 5] << 16)
                                | (page[entryAbs + keyLen + 6] <<  8)
                                |  page[entryAbs + keyLen + 7];
                    }
                    var raw = new byte[entryLen];
                    Array.Copy(page, entryAbs, raw, 0, entryLen);
                    result.Add(new RawEntry(key, raw, (pgBE << 16) | row, subPage));
                }
                lastStart = endOffset;
            }
        }
        return result;
    }

    private void WriteEntries(int pageNumber, List<RawEntry> entries, JetFormat format, bool isLeaf)
    {
        byte[] page = _file.ReadPage(pageNumber);

        // Clear entry mask + entries area.
        int entryMaskPos = format.OffsetIndexEntryMask;
        int maskLen      = format.SizeIndexEntryMask;
        int entriesPos   = entryMaskPos + maskLen;
        for (int i = 0; i < maskLen; i++) page[entryMaskPos + i] = 0;
        int areaSize = format.PageSize - entriesPos;
        for (int i = 0; i < areaSize; i++) page[entriesPos + i] = 0;

        int cursor = 0;
        foreach (var e in entries)
        {
            Array.Copy(e.RawBytes, 0, page, entriesPos + cursor, e.RawBytes.Length);
            cursor += e.RawBytes.Length;
            int endPos = cursor;
            page[entryMaskPos + endPos / 8] |= (byte)(1 << (endPos % 8));
        }
        ByteUtil.PutShort(page, 2, (short)(areaSize - cursor));
        // Re-establish page-type bytes (BuildEmptyIndexPage sets them, but we read
        // existing pages on the write path).
        page[0] = isLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        _file.WritePage(pageNumber, page);
    }

    /// <summary>
    /// Inserts <paramref name="entry"/> into <paramref name="entries"/> at the
    /// position that keeps the list sorted ascending by key bytes.
    /// </summary>
    private static void InsertSortedLeaf(List<RawEntry> entries, RawEntry entry)
    {
        int lo = 0, hi = entries.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (CompareBytes(entries[mid].KeyBytes, entry.KeyBytes) <= 0) lo = mid + 1;
            else hi = mid;
        }
        entries.Insert(lo, entry);
    }

    // ── Entry builders ────────────────────────────────────────────────────────

    private static RawEntry BuildLeafEntry(byte[] keyBytes, int rowPointer)
    {
        int pageNum = (rowPointer >> 16) & 0xFFFFFF;
        int rowNum  = rowPointer & 0xFF;
        var raw = new byte[keyBytes.Length + LeafTrailerLen];
        Array.Copy(keyBytes, raw, keyBytes.Length);
        raw[keyBytes.Length    ] = (byte)((pageNum >> 16) & 0xFF);
        raw[keyBytes.Length + 1] = (byte)((pageNum >>  8) & 0xFF);
        raw[keyBytes.Length + 2] = (byte) (pageNum        & 0xFF);
        raw[keyBytes.Length + 3] = (byte)  rowNum;
        return new RawEntry(keyBytes, raw, rowPointer, 0);
    }

    private static RawEntry BuildNodeEntry(byte[] keyBytes, int subPage)
    {
        // For nodes, rowId (first 4 trailer bytes) is unused for descent — we
        // leave it as 0 since IndexReader doesn't compare against it.
        var raw = new byte[keyBytes.Length + NodeTrailerLen];
        Array.Copy(keyBytes, raw, keyBytes.Length);
        // bytes [keyLen..keyLen+3] left as 0 (rowId)
        raw[keyBytes.Length + 4] = (byte)((subPage >> 24) & 0xFF);
        raw[keyBytes.Length + 5] = (byte)((subPage >> 16) & 0xFF);
        raw[keyBytes.Length + 6] = (byte)((subPage >>  8) & 0xFF);
        raw[keyBytes.Length + 7] = (byte) (subPage        & 0xFF);
        return new RawEntry(keyBytes, raw, 0, subPage);
    }

    // ── TDEF root-page patch ──────────────────────────────────────────────────

    /// <summary>
    /// After a root-page change (initial leaf-to-node promotion), update the
    /// TDEF's index column block so the new root persists. Layout per Jackcess:
    /// header + numIndexes×SizeIndexDefinition + numCols×SizeColumnHeader
    /// + column-names → first index column block at that offset; the root-page
    /// field is at <c>blockStart + SkipBeforeIndex + 30 + 4</c>.
    /// </summary>
    private void PatchTdefRoot(TableDefinition table, int newRoot)
    {
        var format = _file.Format;
        byte[] page = _file.ReadPage(table.TdefPageNumber);

        int numIndexes = ByteUtil.GetInt(page, format.TdefOffsetNumIndexes);
        if (numIndexes < 1) return;
        int numCols    = ByteUtil.GetShort(page, format.TdefOffsetNumCols);
        int colHdrSize = format.SizeColumnHeader;

        int colDefStart  = format.SizeTdefHeader + numIndexes * format.SizeIndexDefinition;
        int colNamesPos  = colDefStart + numCols * colHdrSize;

        // Walk past column names to land on the first index column block.
        int pos = colNamesPos;
        for (int i = 0; i < numCols && pos + format.SizeNameLength <= page.Length; i++)
        {
            int nameLen = format.SizeNameLength == 2
                ? ByteUtil.GetShort(page, pos)
                : page[pos];
            pos += format.SizeNameLength + nameLen;
        }

        // First index column block — rootPage field sits at offset +SkipBeforeIndex+34
        // (4 magic + 10×3 col entries + 4 umap ref).
        int rootFieldOffset = pos + format.SkipBeforeIndex + 30 + 4;
        ByteUtil.PutInt(page, rootFieldOffset, newRoot);
        _file.WritePage(table.TdefPageNumber, page);
    }

    // ── Helpers shared with IndexReader ──────────────────────────────────────

    private static int CompareBytes(byte[] a, byte[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int diff = a[i] - b[i];
            if (diff != 0) return diff;
        }
        return a.Length - b.Length;
    }

    // ── Key encoding ──────────────────────────────────────────────────────────

    private static byte[] EncodeKeyBytes(object value)
    {
        // Single column: ascending flag + per-type value bytes.
        byte[] valueBytes = EncodeColumnValueBytes(value);
        var buf = new byte[1 + valueBytes.Length];
        buf[0] = AscStartFlag;
        Buffer.BlockCopy(valueBytes, 0, buf, 1, valueBytes.Length);
        return buf;
    }

    /// <summary>
    /// Composite-PK encoder. Emits a single <see cref="AscStartFlag"/> prefix
    /// followed by each column's value bytes concatenated in declared order.
    /// Null values are not yet supported — a composite PK with a null component
    /// would need a per-column null-marker byte (0x00 vs 0x7F prefix) which is
    /// out of scope for this slice.
    /// </summary>
    internal static byte[] EncodeCompositeKeyBytes(IReadOnlyList<object?> values)
    {
        if (values is null || values.Count == 0)
            throw new ArgumentException("Composite key needs at least one value.", nameof(values));

        var parts = new byte[values.Count][];
        int total = 1;   // leading flag
        for (int i = 0; i < values.Count; i++)
        {
            if (values[i] is null)
                throw new NotSupportedException(
                    $"Null values in composite primary keys are not yet supported (component #{i}).");
            parts[i] = EncodeColumnValueBytes(values[i]!);
            total += parts[i].Length;
        }
        var buf = new byte[total];
        buf[0] = AscStartFlag;
        int pos = 1;
        for (int i = 0; i < parts.Length; i++)
        {
            Buffer.BlockCopy(parts[i], 0, buf, pos, parts[i].Length);
            pos += parts[i].Length;
        }
        return buf;
    }

    /// <summary>
    /// Per-type value-bytes encoder (without the leading ascending flag).
    /// Used by both <see cref="EncodeKeyBytes"/> and <see cref="EncodeCompositeKeyBytes"/>.
    /// </summary>
    private static byte[] EncodeColumnValueBytes(object value) =>
        value switch
        {
            byte   v => new[] { v },
            short  v => EncodeAscInt16Bytes(v),
            int    v => EncodeAscInt32Bytes(v),
            long   v => EncodeAscInt64Bytes(v),
            string s => GeneralLegacyIndexCodes.EncodeText(s, isAscending: true),
            Guid   g => g.ToByteArray(),
            _ => throw new NotSupportedException(
                    $"Primary key encoding for type '{value.GetType().Name}' is not yet supported.")
        };

    private static byte[] EncodeAscInt16Bytes(short value)
    {
        ushort v = (ushort)((ushort)value ^ 0x8000u);
        return new[] { (byte)(v >> 8), (byte)v };
    }

    private static byte[] EncodeAscInt32Bytes(int value)
    {
        uint v = (uint)value ^ 0x80000000u;
        return new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
    }

    private static byte[] EncodeAscInt64Bytes(long value)
    {
        ulong v = (ulong)value ^ 0x8000000000000000ul;
        return new[]
        {
            (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32),
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >>  8), (byte)v
        };
    }

    // ── Page builders ────────────────────────────────────────────────────────

    private static byte[] BuildEmptyIndexPage(JetFormat format, bool isLeaf)
    {
        var page = new byte[format.PageSize];
        page[0] = isLeaf ? JetFormat.PageTypeIndexLeaf : JetFormat.PageTypeIndexNode;
        page[1] = 0x01;
        int entriesAreaSize = format.PageSize - format.OffsetIndexEntryMask - format.SizeIndexEntryMask;
        ByteUtil.PutShort(page, 2, (short)entriesAreaSize);
        ByteUtil.PutInt(page, format.OffsetPrevIndexPage,      NoPage);
        ByteUtil.PutInt(page, format.OffsetNextIndexPage,      NoPage);
        ByteUtil.PutInt(page, format.OffsetChildTailIndexPage, NoPage);
        return page;
    }

    // ── Records ───────────────────────────────────────────────────────────────

    private record struct RawEntry(byte[] KeyBytes, byte[] RawBytes, int RowPtr, int SubPage);

    private record struct LeafSplit(byte[] LeftMaxKey, byte[] RightMaxKey, int NewSiblingPage);
}
