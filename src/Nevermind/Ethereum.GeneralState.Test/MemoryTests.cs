using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class MemoryTest : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}