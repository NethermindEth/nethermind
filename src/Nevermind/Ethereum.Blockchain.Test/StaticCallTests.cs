using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class StaticCallTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "StaticCall" })]
        public void Test(BlockchainTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}