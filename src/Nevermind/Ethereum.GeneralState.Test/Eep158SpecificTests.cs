using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class Eep158SpecificTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "EIP158Specific" })]
        public void Test(GenerateStateTest test)
        {    
            RunTest(test);
        }
    }
}