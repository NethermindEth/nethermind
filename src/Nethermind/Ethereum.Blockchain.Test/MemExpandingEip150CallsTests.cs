using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MemExpandingEip150CallsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stMemExpandingEIP150Calls" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}