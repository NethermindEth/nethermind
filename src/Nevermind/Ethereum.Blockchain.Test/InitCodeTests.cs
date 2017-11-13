using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class InitCodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "InitCodeTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}