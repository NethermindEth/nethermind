// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Measures cache behavior when the working set exceeds the SeqlockCache capacity.
/// The SeqlockCache has 16,384 sets x 2 ways = 32,768 entries for both state and storage.
///
/// When the working set fits in cache → high hit rate, big improvement.
/// When working set overflows cache → eviction pressure, diminishing returns.
///
/// This benchmark tests both regimes to characterize the cache effectiveness curve
/// and identify the "sweet spot" for cross-block caching.
///
/// Parameters:
/// - UniqueRecipients controls the working set size relative to cache capacity.
/// - 1,000: fits comfortably (< 32K entries) → high hit rate
/// - 16,000: near capacity → some eviction
/// - 50,000: exceeds capacity → heavy eviction, measures degradation
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class WorkingSetOverflowBenchmarks
{
    private readonly List<Block> _fixtureBlocks = new(128);
    private Address[] _recipients = null!;
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private OverflowBenchmarkBlockchain _replayChain;

    [Params(50)]
    public int BlockCount { get; set; }

    [Params(64)]
    public int TxPerBlock { get; set; }

    /// <summary>
    /// Working set size relative to SeqlockCache capacity (32K entries).
    /// </summary>
    [Params(1_000, 16_000, 50_000)]
    public int UniqueRecipients { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(UniqueRecipients);

        using OverflowBenchmarkBlockchain fixtureChain = OverflowBenchmarkBlockchain.Create(enablePrewarmer: false);
        UInt256 nonce = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
            fixtureChain.BlockTree.Head!.Header,
            TestItem.PrivateKeyA.Address);

        for (int blockIndex = 0; blockIndex < BlockCount; blockIndex++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction[] transactions = new Transaction[TxPerBlock];

            for (int txIndex = 0; txIndex < TxPerBlock; txIndex++)
            {
                int recipientIndex = (blockIndex * TxPerBlock + txIndex) % _recipients.Length;
                transactions[txIndex] = Build.A.Transaction
                    .WithTo(_recipients[recipientIndex])
                    .WithValue(UInt256.One)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithNonce(nonce)
                    .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                    .TestObject;
                nonce++;
            }

            Block block = fixtureChain.AddBlock(transactions).GetAwaiter().GetResult();
            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }
    }

    [IterationSetup(Target = nameof(NoPrewarmer))]
    public void SetupNoPrewarmer()
    {
        _replayChain = OverflowBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(WithPrewarmer))]
    public void SetupWithPrewarmer()
    {
        _replayChain = OverflowBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public OverflowResult NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public OverflowResult WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private OverflowResult ReplayAllBlocks()
    {
        OverflowBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Chain not initialized.");

        long stateReadsBefore = Nethermind.Db.Metrics.StateTreeReads;
        long stateCacheBefore = Nethermind.Db.Metrics.StateTreeCache;
        long storageReadsBefore = Nethermind.Db.Metrics.StorageTreeReads;
        long storageCacheBefore = Nethermind.Db.Metrics.StorageTreeCache;

        Stopwatch sw = Stopwatch.StartNew();

        Block[] processedBlocks = chain.BranchProcessor.Process(
            chain.BlockTree.Head!.Header,
            _fixtureBlocks,
            ProcessingOptions.None,
            NullBlockTracer.Instance);

        sw.Stop();

        if (processedBlocks is null || processedBlocks.Length != _fixtureBlocks.Count)
        {
            throw new InvalidOperationException(
                $"Expected {_fixtureBlocks.Count} processed blocks, got {processedBlocks?.Length ?? 0}.");
        }

        long gasUsed = 0;
        for (int i = 0; i < processedBlocks.Length; i++)
        {
            gasUsed += processedBlocks[i].Header.GasUsed;
        }

        if (processedBlocks[^1].Header.StateRoot != _expectedFinalStateRoot)
        {
            throw new InvalidOperationException(
                $"State root mismatch. Expected {_expectedFinalStateRoot}, got {processedBlocks[^1].Header.StateRoot}.");
        }

        long stateReads = Nethermind.Db.Metrics.StateTreeReads - stateReadsBefore;
        long stateCacheHits = Nethermind.Db.Metrics.StateTreeCache - stateCacheBefore;
        long storageReads = Nethermind.Db.Metrics.StorageTreeReads - storageReadsBefore;
        long storageCacheHits = Nethermind.Db.Metrics.StorageTreeCache - storageCacheBefore;

        double mgasPerSec = sw.ElapsedTicks == 0 ? 0 : gasUsed / 1_000_000D / sw.Elapsed.TotalSeconds;
        double stateHitRate = stateReads + stateCacheHits == 0 ? 0 : 100.0 * stateCacheHits / (stateReads + stateCacheHits);

        // Per-block averages for easy comparison
        double avgStateReadsPerBlock = (double)stateReads / BlockCount;
        double avgCacheHitsPerBlock = (double)stateCacheHits / BlockCount;

        return new OverflowResult(mgasPerSec, stateReads, stateCacheHits, stateHitRate, avgStateReadsPerBlock, avgCacheHitsPerBlock);
    }

    private static Address[] CreateRecipients(int count)
    {
        Address[] recipients = new Address[count];
        Random random = new(99);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    public readonly record struct OverflowResult(
        double MGasPerSec,
        long StateReads,
        long StateCacheHits,
        double StateHitRatePercent,
        double AvgStateReadsPerBlock,
        double AvgCacheHitsPerBlock)
    {
        public override string ToString() =>
            $"{MGasPerSec:F1} MGas/s | stateReads={StateReads} hits={StateCacheHits} ({StateHitRatePercent:F1}%) | avg/block: reads={AvgStateReadsPerBlock:F0} hits={AvgCacheHitsPerBlock:F0}";
    }

    private sealed class OverflowBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static OverflowBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            OverflowBenchmarkBlockchain chain = new(enablePrewarmer)
            {
                TestTimeout = 300_000
            };
            chain.Build().GetAwaiter().GetResult();
            return chain;
        }

        protected override IEnumerable<IConfig> CreateConfigs()
        {
            BlocksConfig blocksConfig = new()
            {
                MinGasPrice = 0,
                PreWarmStateOnBlockProcessing = _enablePrewarmer,
                CachePrecompilesOnBlockProcessing = true,
                PreWarmStateConcurrency = 0
            };

            return [blocksConfig];
        }
    }
}
