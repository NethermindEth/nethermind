using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class TransactionTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "TransactionTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}