using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroCallsRevertTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsRevert" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}