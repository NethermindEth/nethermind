using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class QuadraticComplexityTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stQuadraticComplexityTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}