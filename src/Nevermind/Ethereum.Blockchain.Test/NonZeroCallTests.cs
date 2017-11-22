using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class NonZeroCallTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stNonZeroCallsTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}