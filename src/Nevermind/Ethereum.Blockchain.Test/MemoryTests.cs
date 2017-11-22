using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MemoryTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stMemoryTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}