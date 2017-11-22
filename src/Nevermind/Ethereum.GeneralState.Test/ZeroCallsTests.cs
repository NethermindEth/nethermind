using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroCallsTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroCallsTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}