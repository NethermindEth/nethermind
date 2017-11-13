using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SpecialTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SpecialTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}