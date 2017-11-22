using Ethereum.Blockchain.Test;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Test
{
    [TestFixture]
    public class GasPricerTest : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "bcGasPricerTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}