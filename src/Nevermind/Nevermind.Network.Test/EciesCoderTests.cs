using Nevermind.Core.Crypto;
using Nevermind.Core.Extensions;
using NUnit.Framework;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class EciesCoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            ICryptoRandom cryptoRandom = new CryptoRandom();
            EciesCoder coder = new EciesCoder(cryptoRandom);

            (var privateKey, var publicKey) = BouncyCrypto.GenerateKeyPair();

            byte[] plainText = {1, 2, 3, 4, 5};
            byte[] cipherText = coder.Encrypt(publicKey.Q, plainText, null); // public(65) | IV(16) | cipher(...)

            byte[] ephemeralPublicKeyBytes = cipherText.Slice(0, 65);
            ECPublicKeyParameters ephemeralPublicKey = BouncyCrypto.WrapPublicKey(ephemeralPublicKeyBytes);
            byte[] iv = cipherText.Slice(65, 16);

            byte[] deciphered = coder.Decrypt(ephemeralPublicKey.Q, privateKey.D, iv, cipherText.Slice(81), null);
            Assert.AreEqual(plainText, deciphered);
        }
    }
}