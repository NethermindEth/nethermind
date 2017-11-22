using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class MemoryTest : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "MemoryTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}