using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ZeroKnowledgeTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ZeroKnowledge" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}