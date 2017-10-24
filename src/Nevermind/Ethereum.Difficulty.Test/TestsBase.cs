using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nevermind.Core;
using Nevermind.Core.Difficulty;
using Nevermind.Core.Encoding;
using Nevermind.Core.Sugar;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    public abstract class TestsBase
    {
        public static IEnumerable<DifficultyTest> Load(string fileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, DifficultyTestJson>, DifficultyTest>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        public static IEnumerable<DifficultyTest> LoadHex(string fileName)
        {
            Stopwatch watch = new Stopwatch();
            IEnumerable<DifficultyTest> tests = TestLoader.LoadFromFile<Dictionary<string, DifficultyTestHexJson>, DifficultyTest>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value))).ToList();
            watch.Stop();
            return tests;
        }

        protected static DifficultyTest ToTest(string fileName, string name, DifficultyTestJson json)
        {
            return new DifficultyTest(
                fileName,
                name,
                json.ParentTimestamp,
                json.ParentDifficulty,
                json.CurrentTimestamp,
                (ulong)json.CurrentBlockNumber,
                json.CurrentDifficulty,
                false);
        }

        private static BigInteger ToBigInteger(string hex)
        {
            hex = hex.Replace("0x", "0");
            return BigInteger.Parse(hex, NumberStyles.HexNumber);
        }

        private static ulong ToUlong(string hex)
        {
            byte[] bytes = Hex.ToBytes(hex);
            return bytes.ToUInt64();
        }

        protected static DifficultyTest ToTest(string fileName, string name, DifficultyTestHexJson json)
        {
            Keccak noUnclesHash = Keccak.OfAnEmptySequenceRlp;

            return new DifficultyTest(
                fileName,
                name,
                ToBigInteger(json.ParentTimestamp),
                ToBigInteger(json.ParentDifficulty),
                ToBigInteger(json.CurrentTimestamp),
                ToUlong(json.CurrentBlockNumber),
                ToBigInteger(json.CurrentDifficulty),
                !string.IsNullOrWhiteSpace(json.ParentUncles) && new Keccak(json.ParentUncles) != noUnclesHash);
        }

        private readonly DifficultyCalculatorFactory _factory = new DifficultyCalculatorFactory();

        protected void RunTest(DifficultyTest test, EthereumNetwork network)
        {
            IDifficultyCalculator calculator = _factory.GetCalculator(network);

            BigInteger difficulty = calculator.Calculate(
                test.ParentDifficulty,
                test.ParentTimestamp,
                test.CurrentTimestamp,
                test.CurrentBlockNumber,
                test.ParentHasUncles);

            Assert.AreEqual(test.CurrentDifficulty, difficulty, test.Name);
        }
    }
}