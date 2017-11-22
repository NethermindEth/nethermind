using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class StaticCallTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "StaticCallTests" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}