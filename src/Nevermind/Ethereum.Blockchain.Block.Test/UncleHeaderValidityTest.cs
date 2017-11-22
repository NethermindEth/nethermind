using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class UncleHeaderValidityTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcUncleHeaderValidityTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}