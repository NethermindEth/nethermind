using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ExampleTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "Example" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}