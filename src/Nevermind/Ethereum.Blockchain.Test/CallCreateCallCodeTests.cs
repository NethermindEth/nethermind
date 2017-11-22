using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallCreateCallCodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCallCreateCallCodeTest" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}