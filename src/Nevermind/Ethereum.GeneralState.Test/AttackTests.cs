using NUnit.Framework;

namespace Ethereum.GeneralState.Test
{
    [TestFixture]
    public class AttackTests : GeneralTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "AttackTest" })]
        public void Test(GenerateStateTest generateStateTest)
        {    
            RunTest(generateStateTest);
        }
    }
}