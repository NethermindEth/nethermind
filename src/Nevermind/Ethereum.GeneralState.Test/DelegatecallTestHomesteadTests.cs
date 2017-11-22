using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class DelegatecallTestHomesteadTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "DelegatecallTestHomestead" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}