using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture][Parallelizable(ParallelScope.All)]
    public class AttackTests : LegacyBlockchainTestBase
    { 
        [TestCaseSource(nameof(LoadTests))]
        public async Task Test(LegacyBlockchainTest test)
        {
            await RunTest(test);
        }

        public static IEnumerable<LegacyBlockchainTest> LoadTests() { return new DirectoryTestsSource("stAttackTest").LoadLegacyTests(); }
    }
}