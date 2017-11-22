using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class UncleTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcUncleTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}