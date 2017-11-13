using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class NonZeroCallTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "NonZeroCallsTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}