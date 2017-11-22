using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class StackTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stStackTests" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}