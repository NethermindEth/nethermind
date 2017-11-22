using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ReturnDataTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stReturnDataTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}