using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class WalletTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "WalletTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}