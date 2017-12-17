using System;
using System.IO;
using Nevermind.Core.Crypto;
using Nevermind.Core.Encoding;
using Nevermind.Core.Potocol;
using NUnit.Framework;

namespace Nevermind.Core.Test
{
    [TestFixture]
    public class SignerTests
    {
        [OneTimeSetUp]
        public void SetUp()
        {
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
        }

        [Test]
        public void Sign_and_recover()
        {
            Signer signer = new Signer(Olympic.Instance, ChainId.Mainnet);
            
            Keccak message = Keccak.Compute("Test message");
            PrivateKey privateKey = new PrivateKey();
            Signature signature = signer.Sign(privateKey, message);
            Assert.AreEqual(privateKey.Address, signer.Recover(signature, message));
        }

        [TestCase("0x9242685bf161793cc25603c231bc2f568eb630ea16aa137d2664ac80388256084f8ae3bd7535248d0bd448298cc2e2071e56992d0774dc340c368ae950852ada1c")]
        [TestCase("0x34ff4b97a0ec8f735f781f250dcd3070a72ddb640072dd39553407d0320db79939e3b080ecaa2e9f248214c6f0811fb4b4ba05b7bcff254c053e47d8513e820900")]
        public void Hex_and_back_again(string hexSignature)
        {
            Signature signature = new Signature(hexSignature);
            string hexAgain = signature.ToString();
            Assert.AreEqual(hexSignature, hexAgain);
        }
    }
}
