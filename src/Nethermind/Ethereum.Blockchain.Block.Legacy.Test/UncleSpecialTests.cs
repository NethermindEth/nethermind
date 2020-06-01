using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Block.Legacy.Test
{
    [TestFixture]
    [Parallelizable(ParallelScope.All)]
    public class UncleSpecialTests : LegacyBlockchainTestBase
    {
        [TestCaseSource(nameof(LoadTests))]
        public async Task Test(LegacyBlockchainTest test)
        {
            await RunTest(test);
        }

        public static IEnumerable<LegacyBlockchainTest> LoadTests() { return new DirectoryTestsSource("bcUncleSpecialTests").LoadLegacyTests(); }
    }
}