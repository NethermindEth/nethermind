using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class TotalDifficultyTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcTotalDifficultyTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}