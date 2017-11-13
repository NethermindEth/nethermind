using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class PreCompiledContracts2Tests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "PreCompiledContracts2" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}