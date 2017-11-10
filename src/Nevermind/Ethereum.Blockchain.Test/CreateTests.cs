using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CreateTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CreateTest" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}