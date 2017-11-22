using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class PreCompiledContractsTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "PreCompiledContracts" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}