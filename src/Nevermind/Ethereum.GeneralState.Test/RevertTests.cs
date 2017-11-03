using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RevertTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RevertTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}