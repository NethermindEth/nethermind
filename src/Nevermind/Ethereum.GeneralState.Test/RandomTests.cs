using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RandomTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Random" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}