using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RefundTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stRefundTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}