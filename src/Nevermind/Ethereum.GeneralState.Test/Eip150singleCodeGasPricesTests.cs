using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class Eip150singleCodeGasPricesTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "EIP150singleCodeGasPrice" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}