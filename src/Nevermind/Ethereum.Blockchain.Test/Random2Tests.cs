using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class Random2Tests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stRandom2" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}