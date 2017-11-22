using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MemoryStressTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stMemoryStressTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}