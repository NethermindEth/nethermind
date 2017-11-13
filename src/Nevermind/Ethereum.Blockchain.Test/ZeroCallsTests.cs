using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ZeroCallsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}