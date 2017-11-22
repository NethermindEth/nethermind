using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ZeroCallsRevertTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stZeroCallsRevert" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}