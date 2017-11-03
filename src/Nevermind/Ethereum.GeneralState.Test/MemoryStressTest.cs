using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class MemoryStressTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryStressTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}