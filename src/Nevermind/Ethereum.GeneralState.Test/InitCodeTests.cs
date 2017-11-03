using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class InitCodeTestTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "InitCodeTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}