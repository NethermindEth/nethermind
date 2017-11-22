using NUnit.Framework;

namespace Ethereum.Blockchain.Test
{
    [TestFixture]
    public class AttackTests : BlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests), new object[] { "stAttackTest" })]
        public void Test(BlockchainTest test)
        {
            RunTest(test);
        }
    }
}