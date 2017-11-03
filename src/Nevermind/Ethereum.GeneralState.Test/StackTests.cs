using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class StackTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "StackTests" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}