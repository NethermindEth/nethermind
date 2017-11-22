using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class TransactionTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stTransactionTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}