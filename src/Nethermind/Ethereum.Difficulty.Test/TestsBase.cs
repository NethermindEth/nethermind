// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Ethereum.Test.Base;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using NUnit.Framework;

namespace Ethereum.Difficulty.Test
{
    public abstract class TestsBase
    {
        public static IEnumerable<DifficultyTests> Load(string fileName)
        {
            return TestLoader.LoadFromFile<Dictionary<string, DifficultyTestJson>, DifficultyTests>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value)));
        }

        public static IEnumerable<DifficultyTests> LoadHex(string fileName)
        {
            Stopwatch watch = new Stopwatch();
            IEnumerable<DifficultyTests> tests = TestLoader.LoadFromFile<Dictionary<string, DifficultyTestHexJson>, DifficultyTests>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value))).ToList();
            watch.Stop();
            return tests;
        }

        protected static DifficultyTests ToTest(string fileName, string name, DifficultyTestJson json)
        {
            return new DifficultyTests(
                fileName,
                name,
                (ulong)json.ParentTimestamp,
                (ulong)json.ParentDifficulty,
                (ulong)json.CurrentTimestamp,
                json.CurrentBlockNumber,
                (ulong)json.CurrentDifficulty,
                false);
        }

        private static BigInteger ToBigInteger(string hex)
        {
            hex = hex.Replace("0x", "0");
            return BigInteger.Parse(hex, NumberStyles.HexNumber);
        }

        private static UInt256 ToUInt256(string hex)
        {
            hex = hex.Replace("0x", "0");
            return Bytes.FromHexString(hex).ToUInt256();
        }

        protected static DifficultyTests ToTest(string fileName, string name, DifficultyTestHexJson json)
        {
            Keccak noUnclesHash = Keccak.OfAnEmptySequenceRlp;

            return new DifficultyTests(
                fileName,
                name,
                (ulong)ToUInt256(json.ParentTimestamp),
                ToUInt256(json.ParentDifficulty),
                (ulong)ToUInt256(json.CurrentTimestamp),
                (long)ToUInt256(json.CurrentBlockNumber),
                ToUInt256(json.CurrentDifficulty),
                !string.IsNullOrWhiteSpace(json.ParentUncles) && new Keccak(json.ParentUncles) != noUnclesHash);
        }

        protected void RunTest(DifficultyTests test, ISpecProvider specProvider)
        {
            EthashDifficultyCalculator calculator = new EthashDifficultyCalculator(specProvider);

            UInt256 difficulty = calculator.Calculate(
                test.ParentDifficulty,
                test.ParentTimestamp,
                test.CurrentTimestamp,
                test.CurrentBlockNumber,
                test.ParentHasUncles);

            Assert.That(difficulty, Is.EqualTo(test.CurrentDifficulty), test.Name);
        }
    }
}
