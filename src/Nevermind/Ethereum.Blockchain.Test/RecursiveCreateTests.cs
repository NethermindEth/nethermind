using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class RecursiveCreateTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RecursiveCreate" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}