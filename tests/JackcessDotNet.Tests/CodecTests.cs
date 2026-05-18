using System.IO;
using Xunit;

namespace JackcessDotNet.Tests;

public sealed class CodecTests
{
    [Fact]
    public void NullCodec_RoundTripsPagesUnchanged()
    {
        var codec = NullCodecHandler.Instance;
        var page = Enumerable.Range(0, 4096).Select(i => (byte)(i & 0xFF)).ToArray();
        var encoded = codec.EncodePage(page, pageNumber: 5);
        var decoded = codec.DecodePage(encoded, pageNumber: 5);
        Assert.Same(page, encoded);
        Assert.Same(page, decoded);
    }

    [Fact]
    public void PageFile_UsesInstalledCodec_OnReadsAndWrites()
    {
        string path = Path.Combine(Path.GetTempPath(), $"codec_{Guid.NewGuid():N}.mdb");
        try
        {
            using var db = Database.Create(path, JetVersion.Jet4);
            // Reach into PageFile via reflection.
            var pageFile = (PageFile)typeof(Database).GetField("_file",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(db)!;

            int decodeCalls = 0, encodeCalls = 0;
            pageFile.Codec = new TestCodec(
                onDecode: () => decodeCalls++,
                onEncode: () => encodeCalls++);

            // Force a couple of read/write operations.
            byte[] page2 = pageFile.ReadPage(2);
            pageFile.WritePage(2, page2);

            Assert.True(decodeCalls >= 1, "Codec.DecodePage should fire on ReadPage");
            Assert.True(encodeCalls >= 1, "Codec.EncodePage should fire on WritePage");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_NonEncryptedFile_Succeeds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"plain_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
                db.CreateTable("X", new[] { new ColumnBuilder("A", DataType.Long).Build() });

            // No exception, no codec injected — the freshly-created file is unencrypted.
            using var reopen = Database.Open(path);
            Assert.Contains("X", reopen.ListTables());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_FileWithCorruptedSystemCatalog_ReportsEncryptedOrCorrupt()
    {
        // Create a real database, then overwrite page 2's leading bytes so it no
        // longer looks like a TableDef. The detector should refuse to open it
        // with a clear message rather than crashing downstream.
        string path = Path.Combine(Path.GetTempPath(), $"enc_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
                db.CreateTable("X", new[] { new ColumnBuilder("A", DataType.Long).Build() });

            // Corrupt page 2's first two bytes (would be 0x02 0x01 in an unencrypted file).
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                fs.Seek(2 * 4096, SeekOrigin.Begin);
                fs.WriteByte(0xDE);
                fs.WriteByte(0xAD);
            }

            var ex = Assert.Throws<NotSupportedException>(() => Database.Open(path));
            Assert.Contains("encrypted or corrupt", ex.Message);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Open_WithPassword_OnUnencryptedFile_IgnoresPassword()
    {
        // The Jet4 codec detects encryption via the verifier bytes at offset 0x42.
        // For an unencrypted file those bytes are all zero, no codec is installed,
        // and the password is silently ignored.
        string path = Path.Combine(Path.GetTempPath(), $"pw_{Guid.NewGuid():N}.mdb");
        try
        {
            using (var db = Database.Create(path, JetVersion.Jet4))
                db.CreateTable("X", new[] { new ColumnBuilder("A", DataType.Long).Build() });

            using var reopen = Database.Open(path, "any-password");
            Assert.Contains("X", reopen.ListTables());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Rc4_ProducesRfcVector_Output()
    {
        // RFC 6229 test vector: key "Key", plaintext "Plaintext" → ciphertext.
        // Verifying RC4 implementation against a published example.
        byte[] key       = System.Text.Encoding.ASCII.GetBytes("Key");
        byte[] plaintext = System.Text.Encoding.ASCII.GetBytes("Plaintext");
        byte[] expected  = new byte[] { 0xBB, 0xF3, 0x16, 0xE8, 0xD9, 0x40, 0xAF, 0x0A, 0xD3 };

        byte[] keystream = JetCryptCodecHandler.Rc4Keystream(key, plaintext.Length);
        var output = new byte[plaintext.Length];
        for (int i = 0; i < plaintext.Length; i++)
            output[i] = (byte)(plaintext[i] ^ keystream[i]);

        Assert.Equal(expected, output);
    }

    [Fact]
    public void Rc4_KnownVector_AttackAtDawn()
    {
        // Classic RC4 example: key "Secret" + plaintext "Attack at dawn".
        byte[] key = System.Text.Encoding.ASCII.GetBytes("Secret");
        byte[] pt  = System.Text.Encoding.ASCII.GetBytes("Attack at dawn");
        byte[] expectedCt = new byte[]
        {
            0x45, 0xA0, 0x1F, 0x64, 0x5F, 0xC3, 0x5B, 0x38,
            0x35, 0x52, 0x54, 0x4B, 0x9B, 0xF5
        };

        byte[] ks = JetCryptCodecHandler.Rc4Keystream(key, pt.Length);
        byte[] ct = new byte[pt.Length];
        for (int i = 0; i < pt.Length; i++) ct[i] = (byte)(pt[i] ^ ks[i]);

        Assert.Equal(expectedCt, ct);
    }

    private sealed class TestCodec : ICodecHandler
    {
        private readonly Action _onDecode;
        private readonly Action _onEncode;
        public TestCodec(Action onDecode, Action onEncode) { _onDecode = onDecode; _onEncode = onEncode; }
        public byte[] DecodePage(byte[] rawPage, int pageNumber) { _onDecode(); return rawPage; }
        public byte[] EncodePage(byte[] page,    int pageNumber) { _onEncode(); return page; }
    }
}
