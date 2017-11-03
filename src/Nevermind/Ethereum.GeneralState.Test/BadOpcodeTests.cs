using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class BadOpcodeTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "BadOpcode" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}