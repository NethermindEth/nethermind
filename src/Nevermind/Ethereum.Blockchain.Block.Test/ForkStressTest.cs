using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class ForkStressTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcForkStressTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}