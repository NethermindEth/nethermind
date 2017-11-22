using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class LogTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "LogTests" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}