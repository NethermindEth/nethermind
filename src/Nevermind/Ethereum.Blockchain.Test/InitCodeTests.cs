using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class InitCodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stInitCodeTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}