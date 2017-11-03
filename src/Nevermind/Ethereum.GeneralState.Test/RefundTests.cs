using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RefundTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RefundTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}