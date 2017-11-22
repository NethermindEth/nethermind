using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class MemExpandingEip150CallsTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemExpandingEIP150Calls" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}