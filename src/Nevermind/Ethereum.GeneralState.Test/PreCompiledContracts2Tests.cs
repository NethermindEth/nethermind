using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class PreCompiledContracts2Tests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "PreCompiledContracts2" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}