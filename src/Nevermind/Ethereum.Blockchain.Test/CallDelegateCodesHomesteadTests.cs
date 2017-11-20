using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallDelegateCodesHomesteadTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallDelegateCodesHomestead" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}