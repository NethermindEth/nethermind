using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallCodeTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallCodes" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}