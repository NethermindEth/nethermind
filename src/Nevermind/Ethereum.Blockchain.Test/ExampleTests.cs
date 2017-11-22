using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class ExampleTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stExample" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}