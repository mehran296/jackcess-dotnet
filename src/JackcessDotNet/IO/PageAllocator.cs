using JackcessDotNet.Util;

namespace JackcessDotNet;

/// <summary>Allocates new pages by appending them to the file.</summary>
public sealed class PageAllocator
{
    private readonly PageFile _file;

    public PageAllocator(PageFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <summary>Appends a blank page and returns its page number.</summary>
    public int AllocatePage()
    {
        int pageNumber = _file.PageCount;
        _file.WritePage(pageNumber, new byte[_file.Format.PageSize]);
        return pageNumber;
    }

    /// <summary>
    /// Allocates a new data page (type 0x01), writes the standard 14-byte header,
    /// and returns its page number.  The caller is responsible for adding the page
    /// to the owning table's usage-map.
    /// </summary>
    public int AllocateDataPage(int tdefPageNumber)
    {
        var format     = _file.Format;
        int pageNumber = AllocatePage();
        var page       = new byte[format.PageSize];

        page[0] = JetFormat.PageTypeData;
        page[1] = 0x01;
        ByteUtil.PutShort(page, JetFormat.OffsetDataFreeSpace, (short)format.DataPageInitialFreeSpace);
        ByteUtil.PutInt  (page, JetFormat.OffsetDataTdefPage,  tdefPageNumber);
        // numRows = 0 (already zero), at format.OffsetDataNumRows (Jet3=8, Jet4=12)

        _file.WritePage(pageNumber, page);
        return pageNumber;
    }

    /// <summary>
    /// Allocates a new Usage-Map page (type 0x05) with two empty inline maps
    /// (owned-pages map at row 0, free-space map at row 1), and returns its page number.
    /// </summary>
    public int AllocateUmapPage()
    {
        var format     = _file.Format;
        int pageNumber = AllocatePage();
        _file.WritePage(pageNumber, UsageMap.CreateUmapPage(format));
        return pageNumber;
    }
}
