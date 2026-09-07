// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Benchmarks.Core
{
    /// <summary>Builds a receipt bloom and folds it into the block bloom, as block processing does.</summary>
    /// <remarks>
    /// Sized and shaped like real receipts: a handful of emitting contracts, one recurring event
    /// signature as topic 0 and distinct topics after it. Note that the logs are built once, so from
    /// the second invocation on every sequence has been hashed before — this reads the warm case, which
    /// is what block processing and repeated log filtering see, not a first-ever hash of each topic.
    /// </remarks>
    public class BloomBenchmarks
    {
        private const int Contracts = 4;
        private const int TopicsPerLog = 3;

        private LogEntry[] _logs;
        private Bloom _blockBloom;

        [Params(8, 64)]
        public int Logs { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            Address[] addresses = new Address[Contracts];
            for (int i = 0; i < addresses.Length; i++)
            {
                addresses[i] = TestItem.GetRandomAddress();
            }

            Hash256 signature = TestItem.KeccakA;

            _logs = new LogEntry[Logs];
            for (int i = 0; i < _logs.Length; i++)
            {
                Hash256[] topics = new Hash256[TopicsPerLog];
                topics[0] = signature;
                for (int j = 1; j < topics.Length; j++)
                {
                    topics[j] = TestItem.GetRandomKeccak();
                }

                _logs[i] = new LogEntry(addresses[i % Contracts], [], topics);
            }

            _blockBloom = new Bloom();
        }

        [Benchmark]
        public Bloom Build()
        {
            Bloom receiptBloom = new();
            receiptBloom.Add(_logs, _blockBloom);
            return receiptBloom;
        }
    }
}
