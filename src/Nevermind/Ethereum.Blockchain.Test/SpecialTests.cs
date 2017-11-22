using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class SpecialTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stSpecialTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}