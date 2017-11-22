using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallDelegateCodesCallCodeHomesteadTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stCallDelegateCodesCallCodeHomestead" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}