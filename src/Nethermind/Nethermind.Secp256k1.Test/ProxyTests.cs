using NUnit.Framework;

namespace Nethermind.Secp256k1.Test
{
    // TODO: test the output values
    [TestFixture]
    public class ProxyTests
    {
        [Test]
        public void Does_not_allow_empty_key()
        {
            byte[] privateKey = new byte[32];
            bool result =  Proxy.VerifyPrivateKey(privateKey);
            Assert.False(result);
        }
        
        [Test]
        public void Does_allow_valid_keys()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            bool result =  Proxy.VerifyPrivateKey(privateKey);
            Assert.True(result);
        }
        
        [Test]
        public void Can_get_compressed_public_key()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] publicKey =  Proxy.GetPublicKey(privateKey, true);
            Assert.AreEqual(33, publicKey.Length);
        }
        
        [Test]
        public void Can_get_uncompressed_public_key()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] publicKey =  Proxy.GetPublicKey(privateKey, false);
            Assert.AreEqual(65, publicKey.Length);
        }
        
        [Test]
        public void Can_sign()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            Assert.AreEqual(64, signature.Length);
            Assert.AreEqual(1, recoveryId);
        }
        
        [Test]
        public void Can_recover_compressed()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            byte[] recovered =  Proxy.RecoverKeyFromCompact(messageHash, signature, recoveryId, true);
            Assert.AreEqual(33, recovered.Length);
        }
        
        [Test]
        public void Can_recover_uncompressed()
        {
            byte[] privateKey = new byte[32];
            privateKey[0] = 1;
            byte[] messageHash = new byte[32];
            messageHash[0] = 1;
            byte[] signature =  Proxy.SignCompact(messageHash, privateKey, out int recoveryId);
            byte[] recovered =  Proxy.RecoverKeyFromCompact(messageHash, signature, recoveryId, false);
            Assert.AreEqual(65, recovered.Length);
        }
    }
}