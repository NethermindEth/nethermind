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
/// Storage-intensive benchmark that deploys contracts with SSTORE/SLOAD operations
/// and calls them repeatedly across blocks. This is where the cross-block storage cache
/// provides the biggest benefit: storage slots read/written in block N remain warm for
/// block N+1 without requiring trie reads.
///
/// The storage cache is NOT epoch-cleared (unlike state cache) - it accumulates across
/// blocks. This means the longer the run, the warmer the cache, and the bigger the
/// improvement over the no-prewarmer baseline.
///
/// Contract runtime code per call: 5 SLOADs + 2 SSTOREs = ~12,500 gas in storage ops.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class StorageContractBenchmarks
{
    private readonly List<Block> _fixtureBlocks = new(128);
    private Hash256 _expectedFinalStateRoot;
    private long _expectedTotalGasUsed;

    private StorageBenchmarkBlockchain _replayChain;

    /// <summary>
    /// Number of contracts to deploy. Each contract maintains independent storage.
    /// </summary>
    [Params(8, 32)]
    public int ContractCount { get; set; }

    /// <summary>
    /// Number of blocks that call the deployed contracts after deployment.
    /// </summary>
    [Params(60)]
    public int CallBlockCount { get; set; }

    /// <summary>
    /// Number of contract calls per block. Calls cycle through the deployed contracts.
    /// </summary>
    [Params(64)]
    public int CallsPerBlock { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _fixtureBlocks.Clear();
        _expectedFinalStateRoot = null;
        _expectedTotalGasUsed = 0;

        // Build fixture blocks: deploy contracts, then call them repeatedly
        using StorageBenchmarkBlockchain fixtureChain = StorageBenchmarkBlockchain.Create(enablePrewarmer: false);
        UInt256 nonce = fixtureChain.WorldStateManager.GlobalStateReader.GetNonce(
            fixtureChain.BlockTree.Head!.Header,
            TestItem.PrivateKeyA.Address);

        // Phase 1: Deploy contracts (one block per batch of deploys)
        byte[] runtimeCode = BuildStorageHeavyRuntimeCode();
        byte[] initCode = Prepare.EvmCode.ForInitOf(runtimeCode).Done;

        Address[] contractAddresses = new Address[ContractCount];
        int deploysPerBlock = Math.Max(1, Math.Min(ContractCount, 16));
        int deployBlockCount = (ContractCount + deploysPerBlock - 1) / deploysPerBlock;

        for (int blockIdx = 0; blockIdx < deployBlockCount; blockIdx++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            int startContract = blockIdx * deploysPerBlock;
            int endContract = Math.Min(startContract + deploysPerBlock, ContractCount);
            int txCount = endContract - startContract;

            Transaction[] deployTxs = new Transaction[txCount];
            for (int i = 0; i < txCount; i++)
            {
                int contractIdx = startContract + i;
                contractAddresses[contractIdx] = ContractAddress.From(TestItem.PrivateKeyA.Address, nonce);
                deployTxs[i] = Build.A.Transaction
                    .WithCode(initCode)
                    .WithGasLimit(500_000)
                    .WithNonce(nonce)
                    .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                    .TestObject;
                nonce++;
            }

            Block block = fixtureChain.AddBlock(deployTxs).GetAwaiter().GetResult();
            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }

        // Phase 2: Call contracts repeatedly across many blocks
        for (int blockIdx = 0; blockIdx < CallBlockCount; blockIdx++)
        {
            IReleaseSpec spec = fixtureChain.SpecProvider.GetSpec(fixtureChain.BlockTree.Head!.Header);
            Transaction[] callTxs = new Transaction[CallsPerBlock];
            for (int txIdx = 0; txIdx < CallsPerBlock; txIdx++)
            {
                int contractIdx = (blockIdx * CallsPerBlock + txIdx) % ContractCount;
                callTxs[txIdx] = Build.A.Transaction
                    .WithTo(contractAddresses[contractIdx])
                    .WithGasLimit(100_000)
                    .WithNonce(nonce)
                    .SignedAndResolved(TestItem.PrivateKeyA, spec.IsEip155Enabled)
                    .TestObject;
                nonce++;
            }

            Block block = fixtureChain.AddBlock(callTxs).GetAwaiter().GetResult();
            _fixtureBlocks.Add(block);
            _expectedFinalStateRoot = block.Header.StateRoot;
            _expectedTotalGasUsed += block.Header.GasUsed;
        }
    }

    [IterationSetup(Target = nameof(NoPrewarmer))]
    public void SetupNoPrewarmer()
    {
        _replayChain = StorageBenchmarkBlockchain.Create(enablePrewarmer: false);
    }

    [IterationSetup(Target = nameof(WithPrewarmer))]
    public void SetupWithPrewarmer()
    {
        _replayChain = StorageBenchmarkBlockchain.Create(enablePrewarmer: true);
    }

    [IterationCleanup]
    public void Cleanup()
    {
        _replayChain?.Dispose();
        _replayChain = null;
    }

    [Benchmark(Baseline = true)]
    public StorageResult NoPrewarmer()
    {
        return ReplayAllBlocks();
    }

    [Benchmark]
    public StorageResult WithPrewarmer()
    {
        return ReplayAllBlocks();
    }

    private StorageResult ReplayAllBlocks()
    {
        StorageBenchmarkBlockchain chain = _replayChain ?? throw new InvalidOperationException("Chain not initialized.");

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
        double storageHitRate = storageReads + storageCacheHits == 0 ? 0 : 100.0 * storageCacheHits / (storageReads + storageCacheHits);

        return new StorageResult(mgasPerSec, stateReads, stateCacheHits, storageReads, storageCacheHits, storageHitRate);
    }

    /// <summary>
    /// Builds a contract runtime code that performs heavy storage operations:
    /// - 5 SLOADs (read slots 0-4)
    /// - 2 SSTOREs (write slot 0 as counter, write slot 5 as accumulator)
    /// Each call costs ~12,500 gas in storage operations alone.
    /// Slots accessed are always the same → perfect temporal locality across blocks.
    /// </summary>
    private static byte[] BuildStorageHeavyRuntimeCode()
    {
        return Prepare.EvmCode
            // Read counter from slot 0, increment, write back
            .PushData(0)
            .Op(Instruction.SLOAD)
            .PushData(1)
            .Op(Instruction.ADD)
            .Op(Instruction.DUP1)
            .PushData(0)
            .Op(Instruction.SSTORE)
            // Read slots 1-4 (simulate contract state reads across blocks)
            .PushData(1).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(2).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(3).Op(Instruction.SLOAD).Op(Instruction.POP)
            .PushData(4).Op(Instruction.SLOAD).Op(Instruction.POP)
            // Write accumulator to slot 5
            .PushData(5)
            .Op(Instruction.SSTORE)
            .Op(Instruction.STOP)
            .Done;
    }

    public readonly record struct StorageResult(
        double MGasPerSec,
        long StateReads,
        long StateCacheHits,
        long StorageReads,
        long StorageCacheHits,
        double StorageHitRatePercent)
    {
        public override string ToString() =>
            $"{MGasPerSec:F1} MGas/s | stateReads={StateReads} hits={StateCacheHits} | storageReads={StorageReads} hits={StorageCacheHits} ({StorageHitRatePercent:F1}%)";
    }

    private sealed class StorageBenchmarkBlockchain(bool enablePrewarmer) : BasicTestBlockchain
    {
        private readonly bool _enablePrewarmer = enablePrewarmer;

        public static StorageBenchmarkBlockchain Create(bool enablePrewarmer)
        {
            StorageBenchmarkBlockchain chain = new(enablePrewarmer)
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
