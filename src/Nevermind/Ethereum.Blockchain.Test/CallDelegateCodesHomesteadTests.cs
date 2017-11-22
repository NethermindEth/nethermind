using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallDelegateCodesHomesteadTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCallDelegateCodesHomestead" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}