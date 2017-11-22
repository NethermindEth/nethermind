using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class BugTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stBugs" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}