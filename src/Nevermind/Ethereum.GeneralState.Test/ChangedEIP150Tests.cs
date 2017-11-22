using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class ChangedEIP150Tests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "ChangedEIP150" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}