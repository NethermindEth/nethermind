using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class PreCompiledContractsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "PreCompiledContracts" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}