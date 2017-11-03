using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CallCreateCallCodeTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallCreateCallCodeTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}