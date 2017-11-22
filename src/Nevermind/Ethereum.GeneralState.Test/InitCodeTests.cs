using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class InitCodeTestTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "InitCodeTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}