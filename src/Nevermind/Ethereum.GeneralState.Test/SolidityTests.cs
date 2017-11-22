using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class SolidityTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SolidityTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}