using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class CreateTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "CreateTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}