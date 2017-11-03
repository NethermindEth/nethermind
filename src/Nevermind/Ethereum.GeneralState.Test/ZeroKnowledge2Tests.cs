using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroKnowledge2Tests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroKnowledge2" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}