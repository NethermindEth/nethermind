using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CreateTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCreateTest" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}