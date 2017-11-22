using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class BadOpcodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stBadOpcode" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}