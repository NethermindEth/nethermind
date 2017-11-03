using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class WalletTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "WalletTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}