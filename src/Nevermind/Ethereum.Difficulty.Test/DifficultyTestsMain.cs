using System.Collections.Generic;
using Nevermind.Core;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    [Parallelizable(ParallelScope.None)]
    public class DifficultyTestsMain : TestsBase
    {
        public static IEnumerable<DifficultyTest> LoadBasicTests()
        {
            return Load("difficulty.json");
        }

        public static IEnumerable<DifficultyTest> LoadMainNetworkTests()
        {
            return LoadHex("difficultyMainNetwork.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomMainNetworkTests()
        {
            return LoadHex("difficultyCustomMainNetwork.json");
        }

        [TestCaseSource(nameof(LoadBasicTests))]
        public void MainNetwork1(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Main);
        }

        [TestCaseSource(nameof(LoadCustomMainNetworkTests))]
        public void MainNetwork2(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Main);
        }

        [TestCaseSource(nameof(LoadMainNetworkTests))]
        public void MainNetwork3(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Main);
        }
    }
}