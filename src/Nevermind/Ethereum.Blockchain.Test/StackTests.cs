using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class StackTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "StackTests" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}