using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class SystemOperationsTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "SystemOperationsTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}