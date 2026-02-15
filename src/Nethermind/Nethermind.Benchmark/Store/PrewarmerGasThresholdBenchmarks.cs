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
/// Focused benchmark for validating the MinPrewarmBlockGas threshold in BlockCachePreWarmer.
///
/// Sweeps TxPerBlock to produce varying gas levels per block:
///   5 tx  = 105,000 gas   (well below 1M threshold → prewarmer skipped)
///  24 tx  = 504,000 gas   (below threshold → prewarmer skipped)
///  48 tx  = 1,008,000 gas (just above threshold → prewarmer runs)
///  96 tx  = 2,016,000 gas (above threshold → prewarmer runs)
/// 192 tx  = 4,032,000 gas (well above threshold → prewarmer runs, amortized overhead)
///
/// For simple ETH transfers: gas per tx = GasCostOf.Transaction = 21,000.
///
/// Expected result: NoPrewarmer and WithPrewarmer should be similar for low tx counts
/// (because the threshold prevents prewarmer from running). At higher tx counts,
/// WithPrewarmer should outperform NoPrewarmer.
///
/// The crossover point helps validate the threshold value.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class PrewarmerGasThresholdBenchmarks
{
    private const int RecipientCount = 512;
    private const int BlockCount = 50;

    private readonly List<Block> _fixtureBlocks = new(BlockCount);
    private Address[] _recipients = null!;
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private ThresholdBenchmarkBlockchain _replayChain;

    /// <summary>
    /// Number of transactions per block. Controls gas per block:
    /// gas = TxPerBlock * 21,000 (GasCostOf.Transaction for simple transfers).
    /// </summary>
    [Params(5, 24, 48, 96, 192)]
    public int TxPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(RecipientCount);

        using ThresholdBenchmarkBlockchain fixtureChain = ThresholdBenchmarkBlockchain.Create(enablePrewarmer: false);
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
        _replayChain = ThresholdBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(WithPrewarmer))]
    public void SetupWithPrewarmer()
    {
        _replayChain = ThresholdBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public ThresholdResult NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public ThresholdResult WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private ThresholdResult ReplayAllBlocks()
    {
        ThresholdBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Chain not initialized.");

        long stateReadsBefore = Nethermind.Db.Metrics.StateTreeReads;
        long stateCacheBefore = Nethermind.Db.Metrics.StateTreeCache;

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
        long gasPerBlock = gasUsed / BlockCount;
        double mgasPerSec = sw.ElapsedTicks == 0 ? 0 : gasUsed / 1_000_000D / sw.Elapsed.TotalSeconds;
        double stateHitRate = stateReads + stateCacheHits == 0 ? 0 : 100.0 * stateCacheHits / (stateReads + stateCacheHits);

        return new ThresholdResult(gasPerBlock, mgasPerSec, stateReads, stateCacheHits, stateHitRate);
    }

    private Transaction[] BuildBlockTransactions(int blockIndex, bool isEip155Enabled, ref UInt256 nonce)
    {
        Transaction[] transactions = new Transaction[TxPerBlock];
        for (int txIndex = 0; txIndex < transactions.Length; txIndex++)
        {
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
        Random random = new(53);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    public readonly record struct ThresholdResult(
        long GasPerBlock,
        double MGasPerSec,
        long StateReads,
        long StateCacheHits,
        double StateHitRatePercent)
    {
        public override string ToString() =>
            $"gasPerBlock={GasPerBlock} | {MGasPerSec:F1} MGas/s | stateReads={StateReads} hits={StateCacheHits} ({StateHitRatePercent:F1}%)";
    }

    private sealed class ThresholdBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static ThresholdBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            ThresholdBenchmarkBlockchain chain = new(enablePrewarmer)
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
