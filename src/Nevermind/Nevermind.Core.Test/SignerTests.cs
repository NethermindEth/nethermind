using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nevermind.Core.Encoding;
using Nevermind.Core.Signing;

namespace Nevermind.Core.Test
{
    [TestClass]
    public class SignerTests
    {
        [TestMethod]
        public void Sign_and_recover()
        {
            Keccak message = Keccak.Compute("Test message");
            PrivateKey privateKey = new PrivateKey();
            Signature signature = Signer.Sign(privateKey, message);
            Assert.AreEqual(privateKey.Address, Signer.RecoverSignerAddress(signature, message));
        }
    }
}
