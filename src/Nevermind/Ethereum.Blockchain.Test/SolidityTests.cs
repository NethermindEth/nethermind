using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SolidityTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stSolidityTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}