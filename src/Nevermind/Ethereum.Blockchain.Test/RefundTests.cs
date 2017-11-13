using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RefundTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RefundTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}