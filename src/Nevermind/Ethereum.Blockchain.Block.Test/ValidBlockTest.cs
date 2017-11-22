using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class ValidBlockTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcValidBlockTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}