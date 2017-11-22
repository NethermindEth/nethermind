using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RandomTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Random" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}