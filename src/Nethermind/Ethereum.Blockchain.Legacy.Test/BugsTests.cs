using System.Collections.Generic;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture][Parallelizable(ParallelScope.All)]
    public class BugsTets : GeneralStateTestBase
    { 
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }

        public static IEnumerable<GeneralStateTest> LoadTests() 
        {
            var loader = new DirectoryTestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stBugs");
            return (IEnumerable<GeneralStateTest>)loader.LoadTests();
        }
    }
}