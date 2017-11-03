using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class PreCompiledContractsTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "PreCompiledContracts" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}