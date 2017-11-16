using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsOlimpic : TestsBase
    {
        public static IEnumerable<DifficultyTest> LoadOlimpicTests()
        {
            return LoadHex("difficultyOlimpic.json");
        }

        [TestCaseSource(nameof(LoadOlimpicTests))]
        public void Olimpic(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Olympic);
        }
    }
}