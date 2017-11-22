using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SystemOperationsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stSystemOperationsTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}