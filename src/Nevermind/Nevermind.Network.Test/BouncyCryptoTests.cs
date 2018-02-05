using Nevermind.Core.Crypto;
using NUnit.Framework;

namespace Nevermind.Network.Test
{
    [TestFixture]
    public class BouncyCryptoTests
    {
        [Test]
        public void Can_calculate_agreement()
        {
            CryptoRandom random = new CryptoRandom();
            PrivateKey privateKey1 = new PrivateKey(random.GenerateRandomBytes(32));
            PrivateKey privateKey2 = new PrivateKey(random.GenerateRandomBytes(32));

            byte[] sharedSecret1 = BouncyCrypto.Agree(privateKey1, privateKey2.PublicKey);
            byte[] sharedSecret2 = BouncyCrypto.Agree(privateKey2, privateKey1.PublicKey);

            Assert.AreEqual(sharedSecret1, sharedSecret2);
        }
    }
}