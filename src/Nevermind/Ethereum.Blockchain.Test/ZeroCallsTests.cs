using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ZeroCallsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stZeroCallsTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}