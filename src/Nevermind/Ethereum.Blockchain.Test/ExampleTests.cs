using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ExampleTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Example" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}