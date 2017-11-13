using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CodeCopyTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CodeCopyTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}