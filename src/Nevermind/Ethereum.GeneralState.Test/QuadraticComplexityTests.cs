using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class QuadraticComplexityTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "QuadraticComplexityTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}