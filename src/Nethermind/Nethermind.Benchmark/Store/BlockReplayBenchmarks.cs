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
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Db;
using Nethermind.State;

namespace Nethermind.Benchmarks.Store;

[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class BlockReplayBenchmarks
{
    private const int RecipientCount = 1024;

    private readonly List<Block> _fixtureBlocks = new(128);
    private Address[] _recipients = null!;
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private ReplayBenchmarkBlockchain _replayChain;

    private long _stateReadsDelta;
    private long _stateCacheHitsDelta;
    private long _storageReadsDelta;
    private long _storageCacheHitsDelta;

    [Params(24, 50)]
    public int BlocksToReplay { get; set; }

    [Params(96)]
    public int TransactionsPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;
        _recipients = CreateRecipients(RecipientCount);

        using ReplayBenchmarkBlockchain fixtureChain = ReplayBenchmarkBlockchain.Create(enablePrewarmer: false);
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

    [IterationSetup(Target = nameof(ReplayBlocksWithoutPrewarmer))]
    public void IterationSetupWithoutPrewarmer()
    {
        _replayChain = ReplayBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(ReplayBlocksWithPrewarmer))]
    public void IterationSetupWithPrewarmer()
    {
        _replayChain = ReplayBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public double ReplayBlocksWithoutPrewarmer()
    {
        return ReplayFixture();
    }

    [Benchmark]
    public double ReplayBlocksWithPrewarmer()
    {
        return ReplayFixture();
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

    private double ReplayFixture()
    {
        ReplayBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Replay chain not initialized.");

        long stateReadsBefore = Nethermind.Db.Metrics.StateTreeReads;
        long stateCacheHitsBefore = Nethermind.Db.Metrics.StateTreeCache;
        long storageReadsBefore = Nethermind.Db.Metrics.StorageTreeReads;
        long storageCacheHitsBefore = Nethermind.Db.Metrics.StorageTreeCache;

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
                $"Replay final state root mismatch. Expected {_expectedFinalStateRoot}, got {processedBlocks[^1].Header.StateRoot}.");
        }

        if (gasUsed != _expectedTotalGasUsed)
        {
            throw new InvalidOperationException(
                $"Replay total gas mismatch. Expected {_expectedTotalGasUsed}, got {gasUsed}.");
        }

        _stateReadsDelta = Nethermind.Db.Metrics.StateTreeReads - stateReadsBefore;
        _stateCacheHitsDelta = Nethermind.Db.Metrics.StateTreeCache - stateCacheHitsBefore;
        _storageReadsDelta = Nethermind.Db.Metrics.StorageTreeReads - storageReadsBefore;
        _storageCacheHitsDelta = Nethermind.Db.Metrics.StorageTreeCache - storageCacheHitsBefore;

        if (sw.ElapsedTicks == 0)
        {
            return 0;
        }

        return gasUsed / 1_000_000D / sw.Elapsed.TotalSeconds;
    }

    private static Address[] CreateRecipients(int recipientCount)
    {
        Address[] recipients = new Address[recipientCount];
        Random random = new(17);
        byte[] buffer = new byte[Address.Size];

        for (int i = 0; i < recipients.Length; i++)
        {
            random.NextBytes(buffer);
            recipients[i] = new Address((byte[])buffer.Clone());
        }

        return recipients;
    }

    private sealed class ReplayBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static ReplayBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            ReplayBenchmarkBlockchain chain = new(enablePrewarmer)
            {
                TestTimeout = 120_000
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
