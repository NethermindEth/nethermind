using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class SpecialTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SpecialTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}