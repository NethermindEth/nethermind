using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroKnowledge2Tests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroKnowledge2" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}