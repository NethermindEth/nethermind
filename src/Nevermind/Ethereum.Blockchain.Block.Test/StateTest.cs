using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class StateTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcStateTests" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}