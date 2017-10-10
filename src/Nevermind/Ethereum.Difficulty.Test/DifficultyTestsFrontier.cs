using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsFrontier : TestsBase
    {
        public static IEnumerable<DifficultyTest> LoadFrontierTests()
        {
            return LoadHex("difficultyFrontier.json");
        }

        [TestCaseSource(nameof(LoadFrontierTests))]
        public void Frontier(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Frontier);
        }    
    }
}