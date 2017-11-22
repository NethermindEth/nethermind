using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class RandomBlockhashTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcRandomBlockhashTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}