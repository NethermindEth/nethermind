using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallCodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCallCodes" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}