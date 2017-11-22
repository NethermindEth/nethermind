using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RefundTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RefundTest" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}