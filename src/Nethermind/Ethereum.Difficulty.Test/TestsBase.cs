// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Ethereum.Test.Base;
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
            return TestLoader.LoadFromFile<Dictionary<string, DifficultyTestHexJson>, DifficultyTests>(
                fileName,
                t => t.Select(dtj => ToTest(fileName, dtj.Key, dtj.Value))).ToList();
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

        private static UInt256 ToUInt256(string hex)
        {
            hex = hex.Replace("0x", "0");
            return Bytes.FromHexString(hex).ToUInt256();
        }

        protected static DifficultyTests ToTest(string fileName, string name, DifficultyTestHexJson json)
        {
            Hash256 noUnclesHash = Keccak.OfAnEmptySequenceRlp;

            return new DifficultyTests(
                fileName,
                name,
                (ulong)ToUInt256(json.ParentTimestamp),
                ToUInt256(json.ParentDifficulty),
                (ulong)ToUInt256(json.CurrentTimestamp),
                (long)ToUInt256(json.CurrentBlockNumber),
                ToUInt256(json.CurrentDifficulty),
                !string.IsNullOrWhiteSpace(json.ParentUncles) && new Hash256(json.ParentUncles) != noUnclesHash);
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

    /// <summary>
    /// Generic fixture for difficulty tests that load hex JSON and use a constructor-provided spec provider.
    /// JSON filename is derived from class name: strip "Tests" suffix, lowercase first char, add ".json".
    /// </summary>
    [Parallelizable(ParallelScope.All)]
    public abstract class DifficultyHexTestFixture<TSelf>(ISpecProvider specProvider) : TestsBase
    {
        public static IEnumerable<DifficultyTests> LoadTests() =>
            LoadHex(TestDirectoryHelper.GetJsonFileByConvention<TSelf>("Tests"));

        [TestCaseSource(nameof(LoadTests))]
        public void Test(DifficultyTests test) => RunTest(test, specProvider);
    }
}
