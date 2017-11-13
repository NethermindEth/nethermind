using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class AttackTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "AttackTest" })]
        public void Test(BlockchainTest generateStateTest)
        {
            RunTest(generateStateTest);
        }
    }
}