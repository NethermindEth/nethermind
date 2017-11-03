using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CallDelegateCodesCallCodeHomesteadTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CallDelegateCodesCallCodeHomestead" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}