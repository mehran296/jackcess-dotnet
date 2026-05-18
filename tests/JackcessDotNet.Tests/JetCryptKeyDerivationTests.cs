using Xunit;

namespace JackcessDotNet.Tests;

/// <summary>
/// Tests for <see cref="JetCryptCodecHandler"/>'s public surface: encryption
/// detection from the DB header, codec round-trip property (encode then decode
/// yields the original page), and the password-as-master-key composition.
/// </summary>
public sealed class JetCryptKeyDerivationTests
{
    [Fact]
    public void FromDbHeader_ZeroVerifier_ReturnsNull_NoCodecNeeded()
    {
        // All zeros in the verifier block → file is unencrypted, no codec.
        byte[] header = new byte[256];   // all zeros
        var codec = JetCryptCodecHandler.FromDbHeader(header, "anything");
        Assert.Null(codec);
    }

    [Fact]
    public void FromDbHeader_NonZeroVerifier_ReturnsCodec()
    {
        byte[] header = new byte[256];
        // Plant non-zero verifier bytes at offset 0x42.
        for (int i = 0; i < 14; i++) header[0x42 + i] = (byte)(0xA0 + i);
        var codec = JetCryptCodecHandler.FromDbHeader(header, "password");
        Assert.NotNull(codec);
    }

    [Fact]
    public void FromDbHeader_HeaderTooShort_ReturnsNull()
    {
        byte[] header = new byte[20];   // < 0x42 + 14 = 0x50
        Assert.Null(JetCryptCodecHandler.FromDbHeader(header, "anything"));
    }

    [Fact]
    public void Codec_EncodeThenDecode_YieldsOriginalPage()
    {
        byte[] header = new byte[256];
        for (int i = 0; i < 14; i++) header[0x42 + i] = (byte)(i + 1);
        var codec = JetCryptCodecHandler.FromDbHeader(header, "secret");
        Assert.NotNull(codec);

        // Synthetic page; the codec is a stream cipher, so encode∘decode = identity.
        var original = new byte[4096];
        new Random(42).NextBytes(original);

        var encoded = codec!.EncodePage(original, pageNumber: 7);
        var decoded = codec.DecodePage(encoded,  pageNumber: 7);

        Assert.Equal(original, decoded);
        Assert.NotEqual(original, encoded);   // page actually got transformed
    }

    [Fact]
    public void Codec_DifferentPages_ProduceDifferentCiphertexts()
    {
        byte[] header = new byte[256];
        for (int i = 0; i < 14; i++) header[0x42 + i] = 0xC3;
        var codec = JetCryptCodecHandler.FromDbHeader(header, "k")!;

        var page = new byte[64];
        var ct1 = codec.EncodePage(page, pageNumber: 1);
        var ct2 = codec.EncodePage(page, pageNumber: 2);
        Assert.NotEqual(ct1, ct2);   // page-keyed: same plaintext, different keystreams
    }

    [Fact]
    public void Codec_Page0_IsPassedThrough()
    {
        // Page 0 (DB header) is partly cleartext and never encrypted; the codec
        // must return it byte-for-byte unchanged.
        byte[] header = new byte[256];
        for (int i = 0; i < 14; i++) header[0x42 + i] = 0x55;
        var codec = JetCryptCodecHandler.FromDbHeader(header, "k")!;

        var page0 = new byte[4096];
        new Random(99).NextBytes(page0);
        Assert.Equal(page0, codec.EncodePage(page0, pageNumber: 0));
        Assert.Equal(page0, codec.DecodePage(page0, pageNumber: 0));
    }

    [Fact]
    public void Rc4Keystream_DifferentKeys_ProduceDifferentStreams()
    {
        var a = JetCryptCodecHandler.Rc4Keystream(new byte[] { 1, 2, 3 }, 32);
        var b = JetCryptCodecHandler.Rc4Keystream(new byte[] { 1, 2, 4 }, 32);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Rc4Keystream_SameKeyAndLength_IsDeterministic()
    {
        var a = JetCryptCodecHandler.Rc4Keystream(new byte[] { 9, 9, 9 }, 16);
        var b = JetCryptCodecHandler.Rc4Keystream(new byte[] { 9, 9, 9 }, 16);
        Assert.Equal(a, b);
    }
}
