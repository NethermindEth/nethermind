using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class Random2Tests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Random2" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}