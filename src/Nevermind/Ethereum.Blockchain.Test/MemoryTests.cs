using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class MemoryTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}