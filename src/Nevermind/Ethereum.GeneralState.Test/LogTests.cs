using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class LogTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "LogTests" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}