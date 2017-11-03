using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class NonZeroCallsTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "NonZeroCallsTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}