using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class RecursiveCreateTests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "RecursiveCreate" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}