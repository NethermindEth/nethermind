using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroCallsRevertTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsRevert" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}