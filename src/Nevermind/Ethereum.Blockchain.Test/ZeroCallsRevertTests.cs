using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ZeroCallsRevertTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsRevert" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}