using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RecursiveCreateTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stRecursiveCreate" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}