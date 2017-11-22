using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RevertTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stRevertTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}