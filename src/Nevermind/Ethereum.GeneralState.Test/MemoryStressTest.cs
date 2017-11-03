using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class MemoryStressTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryStressTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}