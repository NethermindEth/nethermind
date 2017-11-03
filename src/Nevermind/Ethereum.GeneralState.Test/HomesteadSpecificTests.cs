using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class HomesteadSpecificTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "HomesteadSpecific" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}