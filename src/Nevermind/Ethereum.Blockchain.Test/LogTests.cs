using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class LogTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "LogTests" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}