using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class QuadraticComplexityTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "QuadraticComplexityTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}