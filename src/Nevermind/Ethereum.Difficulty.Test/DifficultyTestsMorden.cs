using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsMorden : TestsBase
    {
        public static IEnumerable<DifficultyTest> LoadMordenTests()
        {
            return LoadHex("difficultyMorden.json");
        }

        [TestCaseSource(nameof(LoadMordenTests))]
        public void Morden(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Morden);
        }
    }
}