// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;

namespace Nethermind.Benchmarks.Mining
{
    public class EthashHashimotoBenchmarks
    {
        private Ethash _ethash = new Ethash(LimboLogs.Instance);

        private BlockHeader _header;

        private BlockHeader[] _scenarios =
        {
            Build.A.BlockHeader.WithNumber(1).WithDifficulty(100).TestObject,
            Build.A.BlockHeader.WithNumber(1).WithDifficulty(100).TestObject,
        };

        [Params(0, 1)]
        public int ScenarioIndex { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _header = _scenarios[ScenarioIndex];
        }

        [Benchmark]
        public (Keccak, ulong) Improved()
        {
            return _ethash.Mine(_header, 0UL);
        }

        [Benchmark]
        public (Keccak, ulong) Current()
        {
            return _ethash.Mine(_header, 0UL);
        }
    }
}
