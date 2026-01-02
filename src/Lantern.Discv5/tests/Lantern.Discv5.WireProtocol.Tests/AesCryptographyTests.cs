using Lantern.Discv5.WireProtocol.Session;
using NUnit.Framework;

namespace Lantern.Discv5.WireProtocol.Tests;

[TestFixture]
public class AesCryptographyTests
{
    private static readonly AesCrypto AesCrypto = new();
    private static readonly Random Random = new();

    [Test]
    public void AesCtrEncrypt_InvalidKeyOrIvLength_ThrowsArgumentException()
    {
        var invalidMaskingKey = GenerateRandomBytes(8);
        var invalidMaskingIv = GenerateRandomBytes(8);
        var header = GenerateRandomBytes(16);

        Assert.Throws<ArgumentException>(() => AesCrypto.AesCtrEncrypt(invalidMaskingKey, invalidMaskingIv, header));
    }

    [Test]
    public void AesCtrEncryptDecrypt_ShouldEncryptAndDecryptCorrectly()
    {
        var maskingKey = GenerateRandomBytes(16);
        var maskingIv = GenerateRandomBytes(16);
        var header = GenerateRandomBytes(16);
        var encryptedHeader = AesCrypto.AesCtrEncrypt(maskingKey, maskingIv, header);
        Assert.AreNotEqual(header, encryptedHeader);

        var decryptedHeader = AesCrypto.AesCtrDecrypt(maskingKey, maskingIv, encryptedHeader);
        Assert.AreEqual(header, decryptedHeader);
    }

    [Test]
    public void AesGcmEncryptAndDecrypt_ShouldEncryptAndDecryptCorrectly()
    {
        var key = Convert.FromHexString("9f2d77db7004bf8a1a85107ac686990b");
        var nonce = Convert.FromHexString("27b5af763c446acd2749fe8e");
        var msg = Convert.FromHexString("01c20101");
        var ad = Convert.FromHexString("93a7400fa0d6a694ebc24d5cf570f65d04215b6ac00757875e3f3a5f42107903");
        var cipher = AesCrypto.AesGcmEncrypt(key, nonce, msg, ad);
        var decrypted = AesCrypto.AesGcmDecrypt(key, nonce, cipher, ad);
        Assert.IsTrue(msg.SequenceEqual(decrypted));
    }

    private static byte[] GenerateRandomBytes(int length)
    {
        var bytes = new byte[length];
        Random.NextBytes(bytes);
        return bytes;
    }
}
