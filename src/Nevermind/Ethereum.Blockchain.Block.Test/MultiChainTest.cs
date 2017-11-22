using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class MultiChainTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcMultiChainTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}