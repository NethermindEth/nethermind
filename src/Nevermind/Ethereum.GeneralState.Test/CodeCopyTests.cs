using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CodeCopyTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CodeCopyTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}