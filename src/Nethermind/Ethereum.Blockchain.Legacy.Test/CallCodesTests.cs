using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Blockchain.Legacy.Test
{
    [TestFixture][Parallelizable(ParallelScope.All)]
    public class CallCodesTests : GeneralStateTestBase
    { 
        [TestCaseSource(nameof(LoadTests))]
        public void Test(GeneralStateTest test)
        {
            Assert.True(RunTest(test).Pass);
        }
        public static IEnumerable<GeneralStateTest> LoadTests() 
        {
            var loader = new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(), "stCallCodes");
            return (IEnumerable<GeneralStateTest>)loader.LoadTests();
        }
    }
}