using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class DatabaseHeaderTests
{
    [Fact]
    public void Open_FileWithoutAccessSignature_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"bogus_{Guid.NewGuid():N}.mdb");
        try
        {
            // Write a 4 KiB file that's NOT an Access database.
            File.WriteAllBytes(path, new byte[4096]);
            var ex = Assert.Throws<InvalidDataException>(() => Database.Open(path));
            Assert.Contains("Standard Jet/ACE DB", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_RealAccessFile_PassesSignatureCheck()
    {
        // The freshly-created .mdb our library writes from the embedded template
        // must still pass the signature gate.
        string path = Path.Combine(Path.GetTempPath(), $"sig_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
                db.CreateTable("X", new[] { new ColumnBuilder("A", DataType.Long).Build() });

            using var reopen = Database.Open(path);
            Assert.Contains("X", reopen.ListTables());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_TooShortFile_Throws()
    {
        string path = Path.Combine(Path.GetTempPath(), $"short_{Guid.NewGuid():N}.mdb");
        try
        {
            File.WriteAllBytes(path, new byte[8]);
            Assert.Throws<InvalidDataException>(() => Database.Open(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
