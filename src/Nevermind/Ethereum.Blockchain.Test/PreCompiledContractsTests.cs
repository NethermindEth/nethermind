using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class PreCompiledContractsTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stPreCompiledContracts" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}