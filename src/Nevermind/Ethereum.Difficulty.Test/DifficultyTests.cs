using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using JetBrains.Annotations;
using Nevermind.Core;
using Nevermind.Core.Difficulty;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTests
    {
        private readonly DifficultyCalculatorFactory _factory = new DifficultyCalculatorFactory();

        private static DifficultyTest ToTest(string fileName, string name, DifficultyTestJson json)
        {
            return new DifficultyTest(
                fileName,
                name,
                json.ParentTimestamp,
                json.ParentDifficulty,
                json.CurrentTimestamp,
                (ulong) json.CurrentBlockNumber,
                json.CurrentDifficulty);
        }

        private static BigInteger ToBigInteger(string hex)
        {
            byte[] bytes = Hex.ToBytes(hex);
            return bytes.ToUnsignedBigInteger();
        }

        private static ulong ToUlong(string hex)
        {
            byte[] bytes = Hex.ToBytes(hex);
            return bytes.ToUInt64();
        }

        private static DifficultyTest ToTest(string fileName, string name, DifficultyTestHexJson json)
        {
            return new DifficultyTest(
                fileName,
                name,
                ToBigInteger(json.ParentTimestamp),
                ToBigInteger(json.ParentDifficulty),
                ToBigInteger(json.CurrentTimestamp),
                ToUlong(json.CurrentBlockNumber),
                ToBigInteger(json.CurrentDifficulty));
        }

        public static IEnumerable<DifficultyTest> LoadBasicTests()
        {
            return Load("difficulty.json");
        }

        public static IEnumerable<DifficultyTest> LoadFrontierTests()
        {
            return LoadHex("difficultyFrontier.json");
        }

        public static IEnumerable<DifficultyTest> LoadMainNetworkTests()
        {
            return LoadHex("difficultyMainNetwork.json");
        }

        public static IEnumerable<DifficultyTest> LoadHomesteadTests()
        {
            return LoadHex("difficultyHomestead.json");
        }

        public static IEnumerable<DifficultyTest> LoadMordenTests()
        {
            return LoadHex("difficultyMorden.json");
        }

        public static IEnumerable<DifficultyTest> LoadOlimpicTests()
        {
            return LoadHex("difficultyOlimpic.json");
        }

        public static IEnumerable<DifficultyTest> LoadRopstenTests()
        {
            return LoadHex("difficultyRopsten.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomHomesteadTests()
        {
            return LoadHex("difficultyCustomHomestead.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomMainNetworkTests()
        {
            return LoadHex("difficultyCustomMainNetwork.json");
        }

        public static IEnumerable<DifficultyTest> Load(string fileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, DifficultyTestJson>, DifficultyTest>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        public static IEnumerable<DifficultyTest> LoadHex(string fileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, DifficultyTestHexJson>, DifficultyTest>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        [TestCaseSource(nameof(LoadBasicTests))]
        [TestCaseSource(nameof(LoadCustomMainNetworkTests))]
        [TestCaseSource(nameof(LoadMainNetworkTests))]
        public void MainNetwork(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Main);
        }

        [TestCaseSource(nameof(LoadCustomHomesteadTests))]
        [TestCaseSource(nameof(LoadHomesteadTests))]
        public void Homestead(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Homestead);
        }

        [TestCaseSource(nameof(LoadFrontierTests))]
        public void Frontier(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Frontier);
        }

        [TestCaseSource(nameof(LoadRopstenTests))]
        public void Ropsten(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Ropsten);
        }

        [TestCaseSource(nameof(LoadMordenTests))]
        public void Morden(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Morden);
        }

        [TestCaseSource(nameof(LoadOlimpicTests))]
        public void Olimpic(DifficultyTest test)
        {
            RunTest(test, EthereumNetwork.Olimpic);
        }

        private void RunTest(DifficultyTest test, EthereumNetwork network)
        {
            IDifficultyCalculator calculator = _factory.GetCalculator(network);

            BigInteger difficulty = calculator.Calculate(
                test.ParentDifficulty,
                test.ParentTimestamp,
                test.CurrentTimestamp,
                test.CurrentBlockNumber);

            Assert.AreEqual(test.CurrentDifficulty, difficulty, test.Name);
        }

        [UsedImplicitly]
        private class DifficultyTestJson
        {
            public int ParentTimestamp { get; set; }
            public int ParentDifficulty { get; set; }
            public int CurrentTimestamp { get; set; }
            public int CurrentBlockNumber { get; set; }
            public int CurrentDifficulty { get; set; }
        }

        [UsedImplicitly]
        private class DifficultyTestHexJson
        {
            public string ParentTimestamp { get; set; }
            public string ParentDifficulty { get; set; }
            public string CurrentTimestamp { get; set; }
            public string CurrentBlockNumber { get; set; }
            public string CurrentDifficulty { get; set; }
        }

        [DebuggerDisplay("{Name}")]
        public class DifficultyTest
        {
            public DifficultyTest(string fileName, string name, BigInteger parentTimestamp, BigInteger parentDifficulty,
                BigInteger currentTimestamp, ulong currentBlockNumber, BigInteger currentDifficulty)
            {
                Name = name;
                FileName = fileName;
                ParentTimestamp = parentTimestamp;
                ParentDifficulty = parentDifficulty;
                CurrentTimestamp = currentTimestamp;
                CurrentDifficulty = currentDifficulty;
                CurrentBlockNumber = currentBlockNumber;
            }

            public BigInteger ParentTimestamp { get; set; }
            public BigInteger ParentDifficulty { get; set; }
            public BigInteger CurrentTimestamp { get; set; }
            public ulong CurrentBlockNumber { get; set; }
            public BigInteger CurrentDifficulty { get; set; }
            public string Name { get; set; }
            public string FileName { get; set; }

            public override string ToString()
            {
                return string.Concat(CurrentBlockNumber, ".", CurrentTimestamp - ParentTimestamp, ".", Name);
            }
        }
    }
}