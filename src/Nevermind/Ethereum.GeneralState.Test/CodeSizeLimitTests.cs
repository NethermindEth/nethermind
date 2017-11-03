using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CodeSizeLimitTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CodeSizeLimit" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}