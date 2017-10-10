using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsRopsten : TestsBase
    {
        public static IEnumerable<DifficultyTest> LoadRopstenTests()
        {
            return LoadHex("difficultyRopsten.json");
        }

        [TestCaseSource(nameof(LoadRopstenTests))]
        public void Ropsten(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Ropsten);
        }
    }
}