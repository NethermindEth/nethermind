using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CodeCopyTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CodeCopyTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}