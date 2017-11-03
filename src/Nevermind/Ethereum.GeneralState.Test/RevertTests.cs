using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RevertTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RevertTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}