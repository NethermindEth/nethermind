using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class Eip150singleCodeGasPricesTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "EIP150singleCodeGasPrice" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}