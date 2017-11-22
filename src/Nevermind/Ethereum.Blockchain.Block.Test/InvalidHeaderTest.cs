using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class InvalidHeaderTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcInvalidHeaderTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}