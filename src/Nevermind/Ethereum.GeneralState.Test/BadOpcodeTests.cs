using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class BadOpcodeTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "BadOpcode" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}