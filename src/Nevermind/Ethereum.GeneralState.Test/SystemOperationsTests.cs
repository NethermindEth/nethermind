using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class SystemOperationsTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SystemOperationsTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}