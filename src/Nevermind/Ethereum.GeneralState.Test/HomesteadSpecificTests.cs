using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class HomesteadSpecificTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "HomesteadSpecific" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}