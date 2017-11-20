using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class CallDelegateCodesCallCodeHomesteadTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallDelegateCodesCallCodeHomestead" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}