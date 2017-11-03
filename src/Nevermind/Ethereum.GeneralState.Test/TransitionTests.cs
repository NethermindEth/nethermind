using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class TransitionTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "TransitionTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}