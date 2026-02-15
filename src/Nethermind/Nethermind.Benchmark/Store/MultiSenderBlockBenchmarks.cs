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
/// Multi-sender benchmark measuring how sender-lane partitioning in the prewarmer
/// benefits from the cross-block cache. With multiple senders, the prewarmer can
/// parallelize more effectively, and the cross-block cache retains all sender accounts
/// from the previous block's delta replay.
///
/// This benchmark funds multiple senders in the first few blocks, then uses all
/// senders in parallel across subsequent blocks. The cross-block cache benefit
/// scales with sender count because:
/// - More senders = more parallel lanes in prewarmer
/// - All sender accounts cached from previous block = immediate cache hits
/// - Recipient overlap provides additional cache benefit
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class MultiSenderBlockBenchmarks
{
    private static readonly (Nethermind.Crypto.PrivateKey Key, Address Addr)[] Senders =
    [
        (TestItem.PrivateKeyA, TestItem.PrivateKeyA.Address),
        (TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address),
        (TestItem.PrivateKeyC, TestItem.PrivateKeyC.Address),
        (TestItem.PrivateKeyD, TestItem.PrivateKeyD.Address),
    ];

    private readonly List<Block> _fixtureBlocks = new(128);
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;
    private Address[] _recipients = null!;

    private MultiSenderBenchmarkBlockchain _replayChain;

    /// <summary>
    /// Number of senders to use. More senders = more parallel prewarming lanes.
    /// </summary>
    [Params(1, 2, 4)]
    public int SenderCount { get; set; }

    [Params(50)]
    public int BlockCount { get; set; }

    [Params(64)]
    public int TxPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(256);

        using MultiSenderBenchmarkBlockchain fixtureChain = MultiSenderBenchmarkBlockchain.Create(enablePrewarmer: false);

        // Phase 1: Fund all senders from PrivateKeyA (blocks 0..SenderCount-2)
        // PrivateKeyA is funded at genesis; others need funding
        UInt256 nonceA = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
            fixtureChain.BlockTree.Head!.Header,
            TestItem.PrivateKeyA.Address);

        for (int s = 1; s < SenderCount; s++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction fundTx = Build.A.Transaction
                .WithTo(Senders[s].Addr)
                .WithValue((UInt256)1_000_000_000_000_000_000) // 1 ETH
                .WithGasLimit(GasCostOf.Transaction)
                .WithNonce(nonceA)
                .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                .TestObject;
            nonceA++;

            Block fundBlock = fixtureChain.AddBlock(fundTx).GetAwaiter().GetResult();
            _fixtureBlocks.Add(fundBlock);
            _expectedFinalStateRoot = fundBlock.Header.StateRoot;
            _expectedTotalGasUsed += fundBlock.Header.GasUsed;
        }

        // Phase 2: Build blocks with transactions from all senders
        UInt256[] nonces = new UInt256[SenderCount];
        for (int s = 0; s < SenderCount; s++)
        {
            nonces[s] = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
                fixtureChain.BlockTree.Head!.Header,
                Senders[s].Addr);
        }

        for (int blockIdx = 0; blockIdx < BlockCount; blockIdx++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction[] txs = new Transaction[TxPerBlock];

            for (int txIdx = 0; txIdx < TxPerBlock; txIdx++)
            {
                int senderIdx = txIdx % SenderCount;
                int recipientIdx = (blockIdx * TxPerBlock + txIdx) % _recipients.Length;

                txs[txIdx] = Build.A.Transaction
                    .WithTo(_recipients[recipientIdx])
                    .WithValue(UInt256.One)
                    .WithGasLimit(GasCostOf.Transaction)
                    .WithNonce(nonces[senderIdx])
                    .SignedAndResolved(Senders[senderIdx].Key, spec.IsEip155Enabled)
                    .TestObject;
                nonces[senderIdx]++;
            }

            Block block = fixtureChain.AddBlock(txs).GetAwaiter().GetResult();
            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }
    }

    [IterationSetup(Target = nameof(NoPrewarmer))]
    public void SetupNoPrewarmer()
    {
        _replayChain = MultiSenderBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(WithPrewarmer))]
    public void SetupWithPrewarmer()
    {
        _replayChain = MultiSenderBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public MultiSenderResult NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public MultiSenderResult WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private MultiSenderResult ReplayAllBlocks()
    {
        MultiSenderBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Chain not initialized.");

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

        return new MultiSenderResult(mgasPerSec, stateReads, stateCacheHits, storageReads, storageCacheHits, stateHitRate);
    }

    private static Address[] CreateRecipients(int count)
    {
        Address[] recipients = new Address[count];
        Random random = new(77);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    public readonly record struct MultiSenderResult(
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

    private sealed class MultiSenderBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static MultiSenderBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            MultiSenderBenchmarkBlockchain chain = new(enablePrewarmer)
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
