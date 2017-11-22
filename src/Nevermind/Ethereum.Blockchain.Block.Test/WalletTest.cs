using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class WalletTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcWalletTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}