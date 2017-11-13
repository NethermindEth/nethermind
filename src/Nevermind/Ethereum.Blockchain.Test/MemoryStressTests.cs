using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MemoryStressTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryStressTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}