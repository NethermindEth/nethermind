using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class StaticCallTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stStaticCall" })]
        public void Test(BlockchainTest test)
        {    
            RunTest(test);
        }
    }
}