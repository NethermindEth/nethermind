using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class BugTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Bugs" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}