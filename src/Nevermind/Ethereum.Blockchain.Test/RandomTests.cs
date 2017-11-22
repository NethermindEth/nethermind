using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RandomTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stRandom" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}