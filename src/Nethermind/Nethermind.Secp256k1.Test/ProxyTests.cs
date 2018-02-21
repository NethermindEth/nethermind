using NUnit.Framework;

namespace Nethermind.Secp256k1.Test
{
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
    }
}