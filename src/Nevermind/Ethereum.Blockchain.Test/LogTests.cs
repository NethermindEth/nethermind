using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class LogTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stLogTests" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}