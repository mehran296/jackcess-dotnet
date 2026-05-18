namespace JackcessDotNet;

/// <summary>
/// Identifies a Jet/ACE format generation, used to select the right
/// <see cref="JetFormat"/> at file-open time.
/// </summary>
public enum JetVersion
{
    /// <summary>Jet 3 — Access 97 (.mdb, page size 2048).</summary>
    Jet3,
    /// <summary>Jet 4 — Access 2000/2002/2003 (.mdb, page size 4096).</summary>
    Jet4,
    /// <summary>ACE 12 — Access 2007 (.accdb, page size 4096). Unencrypted only today.</summary>
    Jet12,
    /// <summary>ACE 14 — Access 2010 (.accdb).</summary>
    Jet14,
    /// <summary>ACE 16 — Access 2013/2016 (.accdb).</summary>
    Jet16,
    /// <summary>ACE 17 — Access 2019+ (.accdb).</summary>
    Jet17,
}
