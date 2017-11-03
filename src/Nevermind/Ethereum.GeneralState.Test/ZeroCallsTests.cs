using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroCallsTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}