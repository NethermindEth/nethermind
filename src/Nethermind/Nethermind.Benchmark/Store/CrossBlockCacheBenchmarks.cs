// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Core.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

/// <summary>
/// Scaled block replay benchmarks measuring cross-block cache effectiveness.
/// Compares cold (no cache), warm (prewarmer), and various block counts to
/// make the Amdahl-effect visible in micro-benchmark output.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class CrossBlockCacheBenchmarks
{
    private const int RecipientCount = 2048;

    private readonly List<Block> _fixtureBlocks = new(128);
    private Address[] _recipients = null!;
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private CacheBenchmarkBlockchain _replayChain;

    [Params(50, 100)]
    public int BlocksToReplay { get; set; }

    [Params(64)]
    public int TransactionsPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(RecipientCount);

        using CacheBenchmarkBlockchain fixtureChain = CacheBenchmarkBlockchain.Create(enablePrewarmer: false);
        UInt256 nonce = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
            fixtureChain.BlockTree.Head!.Header,
            TestItem.PrivateKeyA.Address);

        for (int blockIndex = 0; blockIndex < BlocksToReplay; blockIndex++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction[] transactions = BuildBlockTransactions(blockIndex, spec.IsEip155Enabled, ref nonce);
            Block block = fixtureChain.AddBlock(transactions).GetAwaiter().GetResult();

            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }
    }

    [IterationSetup(Target = nameof(ReplayBlocks_Cold_NoPrewarmer))]
    public void SetupCold()
    {
        _replayChain = CacheBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(ReplayBlocks_Warm_WithPrewarmer))]
    public void SetupWarm()
    {
        _replayChain = CacheBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public ReplayResult ReplayBlocks_Cold_NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public ReplayResult ReplayBlocks_Warm_WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private ReplayResult ReplayAllBlocks()
    {
        CacheBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Replay chain not initialized.");

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

        double mgasPerSecond = sw.ElapsedTicks == 0 ? 0 : gasUsed / 1_000_000D / sw.Elapsed.TotalSeconds;

        return new ReplayResult(
            mgasPerSecond,
            stateReads,
            stateCacheHits,
            storageReads,
            storageCacheHits);
    }

    private Transaction[] BuildBlockTransactions(int blockIndex, bool isEip155Enabled, ref UInt256 nonce)
    {
        Transaction[] transactions = new Transaction[TransactionsPerBlock];
        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
            int recipientIndex = (blockIndex * TransactionsPerBlock + txIndex) % _recipients.Length;
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
        Random random = new(31);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    public readonly record struct ReplayResult(
        double MGasPerSecond,
        long StateTreeReads,
        long StateCacheHits,
        long StorageTreeReads,
        long StorageCacheHits)
    {
        public override string ToString() =>
            $"{MGasPerSecond:F1} MGas/s | state reads={StateTreeReads} hits={StateCacheHits} | storage reads={StorageTreeReads} hits={StorageCacheHits}";
    }

    private sealed class CacheBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static CacheBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            CacheBenchmarkBlockchain chain = new(enablePrewarmer)
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
