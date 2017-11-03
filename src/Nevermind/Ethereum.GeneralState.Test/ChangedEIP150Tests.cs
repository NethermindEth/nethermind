using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ChangedEIP150Tests : TestsBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ChangedEIP150" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}