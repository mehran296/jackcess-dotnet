# JackcessDotNet

[![NuGet](https://img.shields.io/nuget/v/JackcessDotNet?logo=nuget&label=NuGet)](https://www.nuget.org/packages/JackcessDotNet)
[![Downloads](https://img.shields.io/nuget/dt/JackcessDotNet?logo=nuget&label=Downloads)](https://www.nuget.org/packages/JackcessDotNet)
[![GitHub](https://img.shields.io/badge/GitHub-mehran--ghanizadeh-181717?logo=github)](https://github.com/mehran-ghanizadeh)
[![Repo](https://img.shields.io/badge/repo-jackcess--dotnet-181717?logo=github)](https://github.com/mehran-ghanizadeh/jackcess-dotnet)
[![License](https://img.shields.io/badge/license-Apache--2.0-blue)](LICENSE)

Pure .NET 8 library for reading and writing Microsoft Access (`.mdb` / `.accdb`) files.
No ODBC, no ACE drivers, no native dependencies — runs anywhere .NET 8 runs
(Windows, Linux, macOS, containers).

This is a C# port of the [`spannm/jackcess`](https://github.com/spannm/jackcess) Java
project (a maintained fork of the original [Jackcess](https://jackcess.sourceforge.io/)
by James Ahlborn). The upstream Java project remains the source of truth for the Jet
file format; this port tracks its behaviour and reuses its test corpus for
verification.

## Install

```
dotnet add package JackcessDotNet
```

## Quick start

```csharp
using JackcessDotNet;

// Open an existing .mdb / .accdb (auto-detects version)
using var db = Database.Open("Northwind.mdb");

foreach (var name in db.ListTables())
    Console.WriteLine(name);

var customers = db.GetTable("Customers");
foreach (var row in customers.NewCursor())
    Console.WriteLine($"{row["CustomerID"]}  {row["CompanyName"]}");

// Create a new database, define a schema, insert rows
using var fresh = Database.Create("MyData.mdb", JetVersion.Jet4);
var people = fresh.CreateTable("People", new[]
{
    new ColumnBuilder("Id",   DataType.Long).Build(),
    new ColumnBuilder("Name", DataType.Text).MaxLength(50).Build(),
}, primaryKey: "Id");
people.Insert(new Row { ["Id"] = 1, ["Name"] = "Alice" });
```

## Opening password-protected files

```csharp
// Jet RC4 (.mdb, Access 97 / 2000–2003)
using var db = Database.Open("Confidential.mdb", "passw0rd");

// .accdb encryption — Agile (Office 2010+), ECMA Standard (Office 2007),
// RC4 CryptoAPI (Office 2002–2003), or Non-Standard AES — all auto-detected
// from the EncryptionInfo header.
using var db = Database.Open("Confidential.accdb", "passw0rd");

// Inspect which scheme the codec picked (useful when triaging an unfamiliar file).
byte[] page0 = File.ReadAllBytes("Confidential.accdb").Take(4096).ToArray();
var codec = OfficeCryptCodecHandler.FromDbHeader(page0, "passw0rd");
Console.WriteLine(codec?.Scheme);   // → "Agile Encryption (Office 2010+)"
```

Wrong passwords throw `UnauthorizedAccessException` before any data page is
parsed — no cryptic "page X corrupt" errors.

## Composite (multi-column) primary keys

```csharp
db.CreateTable("Orders", new[]
{
    new ColumnBuilder("CustomerId", DataType.Long).Build(),
    new ColumnBuilder("OrderId",    DataType.Long).Build(),
    new ColumnBuilder("Total",      DataType.Money).Build(),
}, primaryKeyColumns: new[] { "CustomerId", "OrderId" });
```

Up to 10 columns per composite key (Jet's hard limit). The single-column
`primaryKey:` overload still works for the common case.

## Foreign-key enforcement on insert (opt-in)

```csharp
using var db = Database.Open("Northwind.mdb");
db.EnforceForeignKeys = true;                  // default: false

// MSysRelationships drives validation. Any Insert whose FK column doesn't
// match an existing parent row throws InvalidOperationException.
orders.Insert(new Row { ["CustomerID"] = "INVALID", ... });
//                                                    ^ throws "Foreign-key violation in
//                                                              relationship 'CustomersOrders'..."
```

Null FK values are allowed (SQL semantics). Restrict-only — no cascade.

## Importing from `DataTable`, `DataSet`, or `IEnumerable<T>`

Skip the schema boilerplate — the importer infers columns from the source type:

```csharp
using var db = DatabaseImporter.CreateFromDataTable("out.mdb", myDataTable);

// or add into an existing database
db.ImportTable(otherDataTable, tableName: "Extra", primaryKey: "Id");
db.ImportTables(myDataSet);

db.ImportTable<Customer>(customers, primaryKey: "Id");

// Append rows into a pre-existing table (instead of failing):
db.ImportTable(moreRows, options: new ImportOptions { AppendIfExists = true });
```

POCO mapping respects `[Key]`, `[Column("X")]`, `[MaxLength]`, `[NotMapped]`,
and `[DatabaseGenerated(Identity)]`. Long strings (>255 chars) auto-promote
to Memo; large byte arrays (>255 bytes) to OLE. Unmappable types can fall
back to `Memo` via `ImportOptions.FallbackUnmappableToString = true`.

## Exporting back

```csharp
DataTable          dt    = db.ExportToDataTable("Customers");
DataSet            ds    = db.ExportToDataSet();
IEnumerable<Customer> cs = db.ExportToCollection<Customer>("Customers");
```

The `IEnumerable<T>` path is lazy via `yield`, so `Take(n)` stops early without
materialising the whole table.

## Status

| Feature                                           | Status |
| ------------------------------------------------- | ------ |
| Read Jet 3 (Access 97) / Jet 4 (Access 2000–2003) | ✅      |
| Read ACE 12 / 14 / 16 / 17 (`.accdb`)             | ✅      |
| Create new `.mdb` files (Jet 4 / Jet 3)           | ✅      |
| Row CRUD + B-tree indexes (single-column PK)      | ✅      |
| Memo / OLE long values                            | ✅      |
| PropertyMap & MSysRelationships                   | ✅      |
| Password-protected `.mdb` (Jet RC4 codec)         | ✅      |
| Password-protected `.accdb` (Agile Encryption, Office 2010+) | ✅ Read + write¹ |
| Password-protected `.accdb` (ECMA Standard Encryption, Office 2007) | ✅ Read + write |
| Password-protected `.accdb` (RC4 CryptoAPI, Office 2002–2003) | ✅ Read + write |
| Password-protected `.accdb` (Non-Standard AES, compat mode 0) | ✅ Read + write |
| Password-protected `.accdb` (Extensible Encryption) | ❌ External CSP — non-portable |
| Multi-column primary keys                         | ✅      |
| Foreign-key enforcement on insert (opt-in)        | ✅ Restrict-only |
| Queries (`MSysQueries`)                           | ❌ Not yet |

¹ Agile write doesn't recompute the DataIntegrity HMAC, so files modified
through this library round-trip cleanly through `Database.Open(path, password)`
but Office Access may flag a stale integrity hash. Matches the upstream
`jackcess-encrypt` limitation.

## Type round-trip cheat sheet

| CLR type          | Jet column        | Notes                                  |
| ----------------- | ----------------- | -------------------------------------- |
| `bool`            | `Boolean`         |                                        |
| `byte`            | `Byte`            |                                        |
| `short`/`ushort`  | `Int` (16-bit)    | both come back as `short`              |
| `int`/`uint`      | `Long` (32-bit)   | both come back as `int`                |
| `long`/`ulong`    | `Long` (32-bit)   | **truncated to 32 bits**               |
| `float`/`double`  | `Float`/`Double`  |                                        |
| `decimal`         | `Money`           | 4-decimal precision; use `Numeric` for more |
| `DateTime`        | `ShortDateTime`   | `Kind` is dropped                      |
| `DateTimeOffset`  | `ShortDateTime`   | stored as UTC                          |
| `Guid`            | `Guid`            |                                        |
| `string` ≤255     | `Text`            |                                        |
| `string` >255     | `Memo`            | auto-promoted on `MaxLength`           |
| `byte[]` ≤255     | `Binary`          |                                        |
| `byte[]` >255     | `Ole`             | auto-promoted on `MaxLength`           |
| `Nullable<T>`     | underlying `T`    |                                        |
| `enum`            | underlying type   | round-trips back to the enum on read   |

## Version history

See [CHANGELOG.md](CHANGELOG.md) — also shipped inside the `.nupkg`.

## Credits

This project would not exist without the years of reverse-engineering work
done by James Ahlborn (original Jackcess) and Markus Spann (maintained fork
at [`spannm/jackcess`](https://github.com/spannm/jackcess)). The Jet binary
format is documented effectively only through their Java source.

## License

[Apache License 2.0](LICENSE) — same as the upstream Jackcess project.
