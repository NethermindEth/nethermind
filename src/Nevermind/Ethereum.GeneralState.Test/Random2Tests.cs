using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class Random2Tests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Random2" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}