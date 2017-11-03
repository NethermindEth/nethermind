using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class Eip150SpecificTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "EIP150Specific" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}