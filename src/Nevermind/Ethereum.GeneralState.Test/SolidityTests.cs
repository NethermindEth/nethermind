using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class SolidityTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SolidityTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}