using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CodeSizeLimitTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCodeSizeLimit" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}