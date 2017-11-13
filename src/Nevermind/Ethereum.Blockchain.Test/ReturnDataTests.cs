using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ReturnDataTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ReturnDataTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}