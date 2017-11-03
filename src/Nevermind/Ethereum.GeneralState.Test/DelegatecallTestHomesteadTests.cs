using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class DelegatecallTestHomesteadTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "DelegatecallTestHomestead" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}