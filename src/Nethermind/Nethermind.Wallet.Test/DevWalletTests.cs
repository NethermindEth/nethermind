using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Logging;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Wallet.Test
{
    [TestFixture]
    public class DevWalletTests
    {
        [Test]
        public void Has_10_dev_accounts()
        {
            DevWallet wallet = new DevWallet(NullLogManager.Instance);
            Assert.AreEqual(10, wallet.GetAccounts().Length);
        }

        [Test]
        public void Each_account_can_sign_with_simple_key()
        {
            DevWallet wallet = new DevWallet(NullLogManager.Instance);

            for (int i = 1; i <= 10; i++)
            {
                byte[] keyBytes = new byte[32];
                keyBytes[31] = (byte) i;
                PrivateKey key = new PrivateKey(keyBytes);
                Assert.AreEqual(key.Address, wallet.GetAccounts()[i - 1], $"{i}");
            }
        }

        [Test]
        public void Can_sign()
        {
            EthereumSigner signer = new EthereumSigner(new SingleReleaseSpecProvider(LatestRelease.Instance, 99), NullLogManager.Instance);
            DevWallet wallet = new DevWallet(NullLogManager.Instance);

            for (int i = 1; i <= 10; i++)
            {
                Address signerAddress = wallet.GetAccounts()[0];
                Signature sig = wallet.Sign(signerAddress, TestObject.KeccakA);
                Address recovered = signer.RecoverAddress(sig, TestObject.KeccakA);
                Assert.AreEqual(signerAddress, recovered, $"{i}");
            }
        }
    }
}