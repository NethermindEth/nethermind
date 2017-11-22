using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CallCreateCallCodeTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallCreateCallCodeTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}