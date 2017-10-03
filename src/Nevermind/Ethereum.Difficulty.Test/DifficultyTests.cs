using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Nevermind.Core;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    public class DifficultyTests
    {
        private class DifficultyTestJson
        {
            public int ParentTimestamp { get; set; }
            public int ParentDifficulty { get; set; }
            public int CurrentTimestamp { get; set; }
            public int CurrentBlockNumber { get; set; }
            public int CurrentDifficulty { get; set; }
        }

        private class DifficultyTestHexJson
        {
            public string ParentTimestamp { get; set; }
            public string ParentDifficulty { get; set; }
            public string CurrentTimestamp { get; set; }
            public string CurrentBlockNumber { get; set; }
            public string CurrentDifficulty { get; set; }
        }

        private static DifficultyTest ToTest(string fileName, string name, DifficultyTestJson json)
        {
            return new DifficultyTest(
                fileName,
                name,
                json.ParentTimestamp,
                json.ParentDifficulty,
                json.CurrentTimestamp,
                (ulong)json.CurrentBlockNumber,
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
            Array.Reverse(bytes);
            bytes = Bytes.PadRight(bytes, 8);
            ulong result = BitConverter.ToUInt64(bytes, 0);
            return result;
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

        [DebuggerDisplay("{Name}")]
        public class DifficultyTest
        {
            public DifficultyTest(string fileName, string name, BigInteger parentTimestamp, BigInteger parentDifficulty, BigInteger currentTimestamp, ulong currentBlockNumber, BigInteger currentDifficulty)
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

        public static IEnumerable<DifficultyTest> LoadBasicTests()
        {
            return LoadTests("difficulty.json");
        }

        public static IEnumerable<DifficultyTest> LoadFrontierTests()
        {
            return LoadHexTests("difficultyFrontier.json");
        }

        public static IEnumerable<DifficultyTest> LoadMainNetworkTests()
        {
            return LoadHexTests("difficultyMainNetwork.json");
        }

        public static IEnumerable<DifficultyTest> LoadHomesteadTests()
        {
            return LoadHexTests("difficultyHomestead.json");
        }

        public static IEnumerable<DifficultyTest> LoadMordenTests()
        {
            return LoadHexTests("difficultyMorden.json");
        }

        public static IEnumerable<DifficultyTest> LoadOlimpicTests()
        {
            return LoadHexTests("difficultyOlimpic.json");
        }

        public static IEnumerable<DifficultyTest> LoadRopstenTests()
        {
            return LoadHexTests("difficultyRopsten.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomHomesteadTests()
        {
            return LoadHexTests("difficultyCustomHomestead.json");
        }

        public static IEnumerable<DifficultyTest> LoadCustomMainNetworkTests()
        {
            return LoadHexTests("difficultyCustomMainNetwork.json");
        }

        public static IEnumerable<DifficultyTest> LoadTests(string fileName)
        {
            return LoadFromFile<Dictionary<string, DifficultyTestJson>>(fileName, t =>
                t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        public static IEnumerable<DifficultyTest> LoadHexTests(string fileName)
        {
            return LoadFromFile<Dictionary<string, DifficultyTestHexJson>>(fileName, t =>
                t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        private static IEnumerable<DifficultyTest> LoadFromFile<TContainer>(string testFileName,
            Func<TContainer, IEnumerable<DifficultyTest>> testExtractor)
        {
            Assembly assembly = typeof(DifficultyTest).Assembly;
            string[] resourceNames = Assembly.GetExecutingAssembly().GetManifestResourceNames();
            string resourceName = resourceNames.SingleOrDefault(r => r.Contains(testFileName));
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                Assert.NotNull(stream);
                using (StreamReader reader = new StreamReader(stream))
                {
                    string testJson = reader.ReadToEnd();
                    TContainer testSpecs =
                        JsonConvert.DeserializeObject<TContainer>(testJson);
                    return testExtractor(testSpecs);
                }
            }
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

        private static void RunTest(DifficultyTest test, EthereumNetwork network)
        {
            DifficultyCalculatorFactory factory = new DifficultyCalculatorFactory();
            IDifficultyCalculator calculator = factory.GetCalculator(network);

            BigInteger difficulty = calculator.Calculate(
                test.ParentDifficulty,
                test.ParentTimestamp,
                test.CurrentTimestamp,
                test.CurrentBlockNumber);

            Assert.AreEqual(test.CurrentDifficulty, difficulty, test.Name);
        }
    }
}
