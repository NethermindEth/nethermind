using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CodeSizeLimitTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CodeSizeLimit" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}