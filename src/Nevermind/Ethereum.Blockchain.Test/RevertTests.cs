using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RevertTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RevertTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}