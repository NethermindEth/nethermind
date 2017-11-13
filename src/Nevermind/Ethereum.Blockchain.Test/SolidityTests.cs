using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SolidityTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SolidityTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}