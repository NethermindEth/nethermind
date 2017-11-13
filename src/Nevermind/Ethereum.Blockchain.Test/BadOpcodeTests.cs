using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class BadOpcodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "BadOpcode" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}