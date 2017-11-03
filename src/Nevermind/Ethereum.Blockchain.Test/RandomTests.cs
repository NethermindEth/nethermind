using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RandomTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Random" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}