using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class TransactionTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "TransactionTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}