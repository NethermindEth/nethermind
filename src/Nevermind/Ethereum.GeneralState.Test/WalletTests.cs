using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class WalletTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "WalletTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}