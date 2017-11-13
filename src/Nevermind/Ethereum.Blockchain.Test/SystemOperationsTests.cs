using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SystemOperationsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SystemOperationsTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}