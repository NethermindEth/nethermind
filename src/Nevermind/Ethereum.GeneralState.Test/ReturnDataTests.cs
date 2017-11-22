using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ReturnDataTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ReturnDataTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}