using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class TransactionTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "TransactionTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}