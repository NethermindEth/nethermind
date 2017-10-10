using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsHomestead : TestsBase
    {     
        public static IEnumerable<DifficultyTest> LoadHomesteadTests()
        {
            return LoadHex("difficultyHomestead.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomHomesteadTests()
        {
            return LoadHex("difficultyCustomHomestead.json");
        }

        [TestCaseSource(nameof(LoadCustomHomesteadTests))]
        public void Homestead1(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Homestead);
        }

        [TestCaseSource(nameof(LoadHomesteadTests))]
        public void Homestead2(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Homestead);
        }
    }
}