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
    /// signature as topic 0 and distinct topics after it.
    /// <para>
    /// Two cases, because routing <c>GetExtract</c> through a cache moves cost in both directions.
    /// <see cref="Build"/> reuses one log array, so from the second invocation every sequence has been
    /// hashed before: the warm case block processing and repeated log filtering see.
    /// <see cref="BuildCold"/> regenerates every address and topic per invocation, so each sequence is
    /// hashed for the first time and pays a probe and a store on top of the same keccak - the case that
    /// could regress, and the one RPC-supplied filter addresses and topics actually hit.
    /// </para>
    /// </remarks>
    public class BloomBenchmarks
    {
        private const int Contracts = 4;
        private const int TopicsPerLog = 3;

        private LogEntry[] _logs;
        private LogEntry[] _coldLogs;
        private Bloom _blockBloom;

        [Params(8, 64)]
        public int Logs { get; set; }

        [GlobalSetup]
        public void Setup()
        {
            _logs = BuildLogs(recurringSignature: true);
            _blockBloom = new Bloom();
        }

        /// <remarks>
        /// The cold case needs sequences that have never been hashed, so the array is rebuilt per
        /// iteration and the iteration is a single invocation. Setup time is excluded from the
        /// measurement, but a one-invocation iteration is inherently noisier than the warm case - read
        /// it for the presence or absence of a large regression, not for a few percent.
        /// </remarks>
        [IterationSetup(Target = nameof(BuildCold))]
        public void SetupCold() => _coldLogs = BuildLogs(recurringSignature: false);

        private LogEntry[] BuildLogs(bool recurringSignature)
        {
            Address[] addresses = new Address[Contracts];
            for (int i = 0; i < addresses.Length; i++)
            {
                addresses[i] = TestItem.GetRandomAddress();
            }

            Hash256 signature = recurringSignature ? TestItem.KeccakA : TestItem.GetRandomKeccak();

            LogEntry[] logs = new LogEntry[Logs];
            for (int i = 0; i < logs.Length; i++)
            {
                Hash256[] topics = new Hash256[TopicsPerLog];
                topics[0] = recurringSignature ? signature : TestItem.GetRandomKeccak();
                for (int j = 1; j < topics.Length; j++)
                {
                    topics[j] = TestItem.GetRandomKeccak();
                }

                logs[i] = new LogEntry(addresses[i % Contracts], [], topics);
            }

            return logs;
        }

        [Benchmark(Baseline = true)]
        public Bloom Build()
        {
            Bloom receiptBloom = new();
            receiptBloom.Add(_logs, _blockBloom);
            return receiptBloom;
        }

        [Benchmark]
        [InvocationCount(1, unrollFactor: 1)]
        public Bloom BuildCold()
        {
            Bloom receiptBloom = new();
            receiptBloom.Add(_coldLogs, _blockBloom);
            return receiptBloom;
        }
    }
}
