using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class ForgedTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcForgedTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}