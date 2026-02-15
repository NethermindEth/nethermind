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
/// The definitive end-to-end benchmark for cross-block state caching.
/// Processes many blocks through the full pipeline (EVM + state + trie + prewarmer)
/// with varying temporal locality to isolate the cache benefit.
///
/// Key parameter: RecipientPoolSize controls overlap between consecutive blocks.
/// - Small pool (32): every block hits the same accounts → maximum cache benefit
/// - Large pool (4096): recipients cycle slowly → minimal cache benefit
///
/// Run on master branch → get baseline.
/// Run on perf/cross-block-state-caching → see improvement in WithPrewarmer.
/// The NoPrewarmer baseline should be roughly the same on both branches.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class EndToEndBlockProcessingBenchmarks
{
    private readonly List<Block> _fixtureBlocks = new(128);
    private Address[] _recipients = null!;
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private E2EBenchmarkBlockchain _replayChain;

    [Params(100)]
    public int BlockCount { get; set; }

    [Params(128)]
    public int TxPerBlock { get; set; }

    /// <summary>
    /// Controls temporal locality / overlap between consecutive blocks.
    /// Smaller = more overlap = bigger cache benefit.
    /// </summary>
    [Params(32, 2048)]
    public int RecipientPoolSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(RecipientPoolSize);

        using E2EBenchmarkBlockchain fixtureChain = E2EBenchmarkBlockchain.Create(enablePrewarmer: false);
        UInt256 nonce = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
            fixtureChain.BlockTree.Head!.Header,
            TestItem.PrivateKeyA.Address);

        for (int blockIndex = 0; blockIndex < BlockCount; blockIndex++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction[] transactions = BuildBlockTransactions(blockIndex, spec.IsEip155Enabled, ref nonce);
            Block block = fixtureChain.AddBlock(transactions).GetAwaiter().GetResult();

            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }
    }

    [IterationSetup(Target = nameof(NoPrewarmer))]
    public void SetupNoPrewarmer()
    {
        _replayChain = E2EBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(WithPrewarmer))]
    public void SetupWithPrewarmer()
    {
        _replayChain = E2EBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public E2EResult NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public E2EResult WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private E2EResult ReplayAllBlocks()
    {
        E2EBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Replay chain not initialized.");

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

        if (gasUsed != _expectedTotalGasUsed)
        {
            throw new InvalidOperationException(
                $"Gas mismatch. Expected {_expectedTotalGasUsed}, got {gasUsed}.");
        }

        long stateReads = Nethermind.Db.Metrics.StateTreeReads - stateReadsBefore;
        long stateCacheHits = Nethermind.Db.Metrics.StateTreeCache - stateCacheBefore;
        long storageReads = Nethermind.Db.Metrics.StorageTreeReads - storageReadsBefore;
        long storageCacheHits = Nethermind.Db.Metrics.StorageTreeCache - storageCacheBefore;

        double mgasPerSec = sw.ElapsedTicks == 0 ? 0 : gasUsed / 1_000_000D / sw.Elapsed.TotalSeconds;
        double stateHitRate = stateReads + stateCacheHits == 0 ? 0 : 100.0 * stateCacheHits / (stateReads + stateCacheHits);

        return new E2EResult(mgasPerSec, stateReads, stateCacheHits, storageReads, storageCacheHits, stateHitRate);
    }

    private Transaction[] BuildBlockTransactions(int blockIndex, bool isEip155Enabled, ref UInt256 nonce)
    {
        Transaction[] transactions = new Transaction[TxPerBlock];
        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            // Cycle through recipients - smaller pool = more overlap per block
            int recipientIndex = (blockIndex * TxPerBlock + txIndex) % _recipients.Length;
            Address recipient = _recipients[recipientIndex];
            transactions[txIndex] = Build.A.Transaction
                .WithTo(recipient)
                .WithValue(UInt256.One)
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(nonce)
                .SignedAndResolved(TestItem.PrivateKeyA, isEip155Enabled)
                .TestObject;
            nonce++;
        }

        return transactions;
    }

    private static Address[] CreateRecipients(int count)
    {
        Address[] recipients = new Address[count];
        Random random = new(42);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    public readonly record struct E2EResult(
        double MGasPerSec,
        long StateReads,
        long StateCacheHits,
        long StorageReads,
        long StorageCacheHits,
        double StateHitRatePercent)
    {
        public override string ToString() =>
            $"{MGasPerSec:F1} MGas/s | stateReads={StateReads} hits={StateCacheHits} ({StateHitRatePercent:F1}%) | storageReads={StorageReads} hits={StorageCacheHits}";
    }

    private sealed class E2EBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static E2EBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            E2EBenchmarkBlockchain chain = new(enablePrewarmer)
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
