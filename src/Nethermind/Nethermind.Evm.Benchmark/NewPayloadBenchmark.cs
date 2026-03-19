// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Exporters.Json;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Validators;
using Nethermind.Consensus.Withdrawals;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Utils;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.Forks;
using Nethermind.Evm.State;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.TxPool;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Measures engine_newPayload end-to-end latency through the full handler pipeline.
/// Produces blocks on chain A (setup), then replays on fresh chain B (measurement).
/// Each BDN iteration replays the measured blocks on a fresh copy of the template DB.
/// </summary>
[Config(typeof(NewPayloadConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class NewPayloadBenchmark
{
    private const int WarmupBlocks = 30;
    private const int MeasuredBlocks = 500;
    private const int TotalBlocks = WarmupBlocks + MeasuredBlocks;

    [Params(StateBackend.Trie, StateBackend.FlatState)]
    public StateBackend Backend { get; set; }

    private class NewPayloadConfig : ManualConfig
    {
        public NewPayloadConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Default)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcForce(true)
                .WithEnvironmentVariable("DOTNET_GCServer", "0")
                .WithEnvironmentVariable("DOTNET_gcConcurrent", "0"));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    private static readonly Address Erc20Address = Address.FromNumber(0x1000);
    private static readonly Address SwapAddress = Address.FromNumber(0x2000);
    private static readonly Address WriteHeavyAddress = Address.FromNumber(0x3000);
    private static readonly Address ReadHeavyAddress = Address.FromNumber(0x4000);
    private static readonly Address MixedStorageAddress = Address.FromNumber(0x5000);
    private static readonly Address SimpleContractAddress = TestItem.AddressB;

    private static readonly byte[] SimpleContractCode = Prepare.EvmCode
        .PushData(0x01).Op(Instruction.STOP).Done;

    private static readonly byte[] StopCode = [0x00];

    private static readonly AccessList SampleAccessList = new AccessList.Builder()
        .AddAddress(TestItem.AddressC)
        .AddStorage(UInt256.Zero)
        .AddStorage(UInt256.One)
        .AddStorage(new UInt256(2))
        .Build();

    private record PayloadData(ExecutionPayloadV3 Payload, byte[][]? ExecutionRequests);

    // Produced payloads from chain A
    private PayloadData[] _payloads = null!;

    // Template DB directory (chain B after warmup, flushed + compacted)
    private string _templateDbPath = null!;

    // Per-iteration state
    private string _iterationDbPath = null!;
    private BenchmarkMergeBlockchain? _iterationChain;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        // Phase 1: Produce all payloads on chain A (in-memory, no RocksDB needed)
        _payloads = await ProducePayloads(TotalBlocks, Backend);

        // Phase 2: Build template chain B with RocksDB, replay warmup blocks, flush+compact
        _templateDbPath = Path.Combine(Path.GetTempPath(), $"nethermind-newpayload-template-{Guid.NewGuid()}");
        Directory.CreateDirectory(_templateDbPath);

        using (BenchmarkMergeBlockchain templateChain = new())
        {
            await templateChain.BuildChain(BuildChainConfigurer(Backend, _templateDbPath));

            IEngineRpcModule rpc = templateChain.EngineRpcModule;

            // Replay warmup blocks to build up state
            for (int i = 0; i < WarmupBlocks && i < _payloads.Length; i++)
            {
                ExecutionPayloadV3 p = _payloads[i].Payload;
                await rpc.engine_newPayloadV4(p, Array.Empty<byte[]?>(), p.ParentBeaconBlockRoot, _payloads[i].ExecutionRequests);
                await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(p.BlockHash, p.BlockHash, p.BlockHash));
            }

            // Flush and compact so template DB is clean
            FlushAndCompactChain(templateChain);
        }
        // Template chain is disposed, DB files remain on disk
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Pin to a single core to reduce OS scheduler jitter (only during measurement, not setup)
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);

        // Copy template DB to a fresh directory for this iteration
        _iterationDbPath = Path.Combine(Path.GetTempPath(), $"nethermind-newpayload-iter-{Guid.NewGuid()}");
        CopyDirectory(_templateDbPath, _iterationDbPath);

        // Build chain from existing DB — skip genesis processing since state already exists
        _iterationChain = new BenchmarkMergeBlockchain();
        _iterationChain.BuildChain(BuildIterationChainConfigurer(Backend, _iterationDbPath)).GetAwaiter().GetResult();

        // Disable auto-compaction during measurement
        DisableAutoCompactionOnChain(_iterationChain);
    }

    [Benchmark(OperationsPerInvoke = MeasuredBlocks)]
    public async Task ReplayMeasuredBlocks()
    {
        IEngineRpcModule rpc = _iterationChain!.EngineRpcModule;

        for (int i = WarmupBlocks; i < _payloads.Length; i++)
        {
            ExecutionPayloadV3 p = _payloads[i].Payload;
            await rpc.engine_newPayloadV4(p, Array.Empty<byte[]?>(), p.ParentBeaconBlockRoot, _payloads[i].ExecutionRequests);
            await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(p.BlockHash, p.BlockHash, p.BlockHash));
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _iterationChain?.Dispose();
        _iterationChain = null;

        TryDeleteDirectory(_iterationDbPath);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        TryDeleteDirectory(_templateDbPath);
    }

    // -- Phase 1: Produce payloads on chain A ---------------------------------

    private static async Task<PayloadData[]> ProducePayloads(int blockCount, StateBackend backend)
    {
        PrivateKey senderKey = TestItem.PrivateKeyA;

        // Chain A uses in-memory DB — no RocksDB path needed
        using BenchmarkMergeBlockchain chain = new();
        await chain.BuildChain(BuildInMemoryChainConfigurer(backend));

        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 headHash = chain.BlockTree.HeadHash;
        ulong timestamp = chain.BlockTree.Head!.Timestamp;
        int nonce = 0;

        List<PayloadData> payloads = new(blockCount);

        for (int b = 0; b < blockCount; b++)
        {
            timestamp += 12;
            (List<Transaction> txs, int nextNonce) = BuildMixedTransactions(senderKey, nonce, b);
            nonce = nextNonce;

            foreach (Transaction tx in txs)
                chain.TxPool.SubmitTx(tx, TxHandlingOptions.None);

            ForkchoiceStateV1 fcState = new(headHash, headHash, headHash);
            PayloadAttributes attrs = new()
            {
                Timestamp = timestamp,
                PrevRandao = Keccak.Zero,
                SuggestedFeeRecipient = Address.Zero,
                ParentBeaconBlockRoot = Keccak.Zero,
                Withdrawals = Array.Empty<Withdrawal>()
            };

            ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult =
                await rpc.engine_forkchoiceUpdatedV3(fcState, attrs);
            if (fcuResult.Data?.PayloadId is null)
                break;

            await Task.Delay(200);
            ResultWrapper<GetPayloadV5Result?> getResult =
                await rpc.engine_getPayloadV5(Convert.FromHexString(fcuResult.Data.PayloadId[2..]));
            if (getResult.Data is null) break;

            ExecutionPayloadV3 payload = getResult.Data.ExecutionPayload;
            byte[][]? execRequests = getResult.Data.ExecutionRequests;

            await rpc.engine_newPayloadV4(payload, Array.Empty<byte[]?>(), payload.ParentBeaconBlockRoot, execRequests);
            await rpc.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash));

            // Wait for head to advance before producing next block
            int waitMs = 0;
            while (chain.BlockTree.Head?.Number < b + 1 && waitMs < 30_000)
            {
                await Task.Delay(10);
                waitMs += 10;
            }

            if (chain.BlockTree.Head?.Number < b + 1)
                break;

            headHash = chain.BlockTree.HeadHash;

            long chainNonce = (long)chain.StateReader.GetNonce(chain.BlockTree.Head!.Header, senderKey.Address);
            nonce = (int)chainNonce;

            payloads.Add(new PayloadData(payload, execRequests));
        }

        return payloads.ToArray();
    }

    // -- Genesis seeding ------------------------------------------------------

    private static void SeedGenesis(Block block, IWorldState ws, ISpecProvider specProvider)
    {
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        block.Header.GasLimit = 60_000_000;

        ws.AddToBalanceAndCreateIfNotExists(TestItem.PrivateKeyA.Address, UInt256.MaxValue / 2, spec);

        Random rng = new(42);
        byte[] addrBuf = new byte[20];
        byte[] valBuf = new byte[32];

        for (int a = 0; a < 1_000_000; a++)
        {
            rng.NextBytes(addrBuf);
            ws.AddToBalanceAndCreateIfNotExists(new Address(addrBuf.ToArray()), (UInt256)(rng.Next(1, 1_000_000)), spec);
        }

        for (int a = 0; a < 5_000; a++)
        {
            rng.NextBytes(addrBuf);
            Address addr = new(addrBuf.ToArray());
            ws.AddToBalanceAndCreateIfNotExists(addr, 1, spec);
            ws.InsertCode(addr, StopCode, spec);
            for (int s = 0; s < 100; s++)
            {
                rng.NextBytes(valBuf);
                ws.Set(new StorageCell(addr, (UInt256)rng.NextInt64()), valBuf.ToArray());
            }
        }

        ws.CreateAccount(SimpleContractAddress, UInt256.Zero);
        ws.InsertCode(SimpleContractAddress, SimpleContractCode, spec);
        ws.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
        ws.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, spec);
        ws.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
        ws.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, spec);

        ws.CreateAccount(Erc20Address, UInt256.Zero);
        ws.InsertCode(Erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), spec);
        UInt256 senderSlot = StorageBenchmarkContracts.ComputeMappingSlot(TestItem.PrivateKeyA.Address, UInt256.Zero);
        byte[] bigBalance = new byte[32]; ((UInt256)1_000_000_000).ToBigEndian(bigBalance);
        ws.Set(new StorageCell(Erc20Address, senderSlot), bigBalance);
        byte[] recipientBal = new byte[32]; ((UInt256)100).ToBigEndian(recipientBal);
        for (int i = 0; i < 200; i++)
            ws.Set(new StorageCell(Erc20Address, StorageBenchmarkContracts.ComputeMappingSlot(Address.FromNumber((UInt256)(100 + i)), UInt256.Zero)), recipientBal);

        UInt256 swapErc20Slot = StorageBenchmarkContracts.ComputeMappingSlot(SwapAddress, UInt256.Zero);
        byte[] swapErc20Bal = new byte[32]; ((UInt256)1_000_000_000).ToBigEndian(swapErc20Bal);
        ws.Set(new StorageCell(Erc20Address, swapErc20Slot), swapErc20Bal);

        ws.CreateAccount(SwapAddress, UInt256.Zero);
        ws.InsertCode(SwapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(Erc20Address), spec);
        SeedSlot(ws, SwapAddress, 0, 1_000_000_000); SeedSlot(ws, SwapAddress, 1, 1_000_000_000);
        SeedSlot(ws, SwapAddress, 2, 500_000); SeedSlot(ws, SwapAddress, 3, 30);
        SeedSlot(ws, SwapAddress, 4, 1); SeedSlot(ws, SwapAddress, 5, 1);
        SeedSlot(ws, SwapAddress, 6, 1); SeedSlot(ws, SwapAddress, 7, 1_000_000_000);
        UInt256 senderSwapSlot = StorageBenchmarkContracts.ComputeMappingSlot(TestItem.PrivateKeyA.Address, (UInt256)8);
        byte[] swapBal = new byte[32]; ((UInt256)1_000_000).ToBigEndian(swapBal);
        ws.Set(new StorageCell(SwapAddress, senderSwapSlot), swapBal);

        ws.CreateAccount(WriteHeavyAddress, UInt256.Zero);
        ws.InsertCode(WriteHeavyAddress, StorageBenchmarkContracts.BuildStorageWriteHeavyCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, WriteHeavyAddress, s, 1);

        ws.CreateAccount(ReadHeavyAddress, UInt256.Zero);
        ws.InsertCode(ReadHeavyAddress, StorageBenchmarkContracts.BuildStorageReadHeavyCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, ReadHeavyAddress, s, (ulong)(s + 1) * 1000);

        ws.CreateAccount(MixedStorageAddress, UInt256.Zero);
        ws.InsertCode(MixedStorageAddress, StorageBenchmarkContracts.BuildStorageMixedCode(), spec);
        for (int s = 0; s < 8; s++) SeedSlot(ws, MixedStorageAddress, s, (ulong)(s + 1) * 100);
    }

    private static void SeedSlot(IWorldState ws, Address addr, int slot, ulong value)
    {
        byte[] bytes = new byte[32]; ((UInt256)value).ToBigEndian(bytes);
        ws.Set(new StorageCell(addr, (UInt256)slot), bytes);
    }

    // -- Chain configuration --------------------------------------------------

    /// <summary>
    /// Configurer for chain B (measurement) — uses RocksDB at the given path.
    /// </summary>
    private static Action<ContainerBuilder> BuildChainConfigurer(StateBackend backend, string dbPath)
    {
        return builder =>
        {
            builder.AddSingleton<ISpecProvider>(CreateOsakaSpecProvider());
            builder.WithGenesisPostProcessor(SeedGenesis);
            if (backend == StateBackend.Trie)
                builder.Intercept<IFlatDbConfig>(cfg => cfg.Enabled = false);

            // Override MemDbFactory (registered by TestEnvironmentModule) with RocksDbFactory.
            // In Autofac, last-registration-wins, and configurer runs after ConfigureContainer.
            IDbConfig dbConfig = new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 };
            IPruningConfig pruningConfig = new PruningConfig();
            IRocksDbConfigFactory rocksDbConfigFactory = new RocksDbConfigFactory(dbConfig, pruningConfig, new TestHardwareInfo(), LimboLogs.Instance);
            HyperClockCacheWrapper sharedCache = new(dbConfig.SharedBlockCacheSize);
            IDbFactory rocksDbFactory = new RocksDbFactory(rocksDbConfigFactory, dbConfig, sharedCache, LimboLogs.Instance, dbPath);
            builder.AddSingleton<IDbFactory>(rocksDbFactory);

            // Genesis seeding 1M accounts + 500k storage slots on RocksDB is slow — increase timeout
            builder.Intercept<IBlocksConfig>(cfg => cfg.GenesisTimeoutMs = 300_000);
        };
    }

    /// <summary>
    /// Configurer for iteration chains — uses existing RocksDB, skips genesis processing.
    /// The DB already has all state from the template chain built in GlobalSetup.
    /// </summary>
    private static Action<ContainerBuilder> BuildIterationChainConfigurer(StateBackend backend, string dbPath)
    {
        return builder =>
        {
            builder.AddSingleton<ISpecProvider>(CreateOsakaSpecProvider());
            // NO SeedGenesis post-processor — state already exists in DB
            if (backend == StateBackend.Trie)
                builder.Intercept<IFlatDbConfig>(cfg => cfg.Enabled = false);

            // Override MemDbFactory with RocksDbFactory pointing at the iteration DB
            IDbConfig dbConfig = new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 };
            IPruningConfig pruningConfig = new PruningConfig();
            IRocksDbConfigFactory rocksDbConfigFactory = new RocksDbConfigFactory(dbConfig, pruningConfig, new TestHardwareInfo(), LimboLogs.Instance);
            HyperClockCacheWrapper sharedCache = new(dbConfig.SharedBlockCacheSize);
            IDbFactory rocksDbFactory = new RocksDbFactory(rocksDbConfigFactory, dbConfig, sharedCache, LimboLogs.Instance, dbPath);
            builder.AddSingleton<IDbFactory>(rocksDbFactory);

            // Skip genesis processing — state is already seeded in the copied DB
            builder.AddSingleton(new TestBlockchain.Configuration
            {
                SuggestGenesisOnStart = false,
                AddBlockOnStart = false,
            });
        };
    }

    /// <summary>
    /// Configurer for chain A (payload production) — uses in-memory DB.
    /// </summary>
    private static Action<ContainerBuilder> BuildInMemoryChainConfigurer(StateBackend backend)
    {
        return builder =>
        {
            builder.AddSingleton<ISpecProvider>(CreateOsakaSpecProvider());
            builder.WithGenesisPostProcessor(SeedGenesis);
            if (backend == StateBackend.Trie)
                builder.Intercept<IFlatDbConfig>(cfg => cfg.Enabled = false);
        };
    }

    private static ISpecProvider CreateOsakaSpecProvider()
    {
        TestSingleReleaseSpecProvider provider = new(Osaka.Instance);
        provider.TimestampFork = 0;
        provider.TerminalTotalDifficulty = 0;
        provider.UpdateMergeTransitionInfo(0);
        return provider;
    }

    // -- Transaction building -------------------------------------------------

    private static (List<Transaction> txs, int nextNonce) BuildMixedTransactions(PrivateKey senderKey, int nonce, int blockIndex)
    {
        List<Transaction> txs = new(300);

        for (int t = 0; t < 50; t++)
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, TestItem.AddressC, (UInt256)1, 21_000));

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[64];
            Address.FromNumber((UInt256)(100 + (blockIndex * 50 + t) % 200)).Bytes.CopyTo(calldata.AsSpan(12));
            ((UInt256)1).ToBigEndian(calldata.AsSpan(32));
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, Erc20Address, UInt256.Zero, 100_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 1)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, SwapAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 42)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, WriteHeavyAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, ReadHeavyAddress, UInt256.Zero, 100_000, calldata));
        }

        for (int t = 0; t < 50; t++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(blockIndex * 50 + t + 7)).ToBigEndian(calldata);
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, MixedStorageAddress, UInt256.Zero, 60_000, calldata));
        }

        for (int t = 0; t < 15; t++)
        {
            txs.Add(Build.A.Transaction
                .WithType(TxType.AccessList).WithNonce((UInt256)nonce++).WithTo(TestItem.AddressC)
                .WithValue((UInt256)1).WithGasLimit(50_000).WithGasPrice(20.GWei)
                .WithAccessList(SampleAccessList).SignedAndResolved(senderKey).TestObject);
        }

        for (int t = 0; t < 5; t++)
        {
            byte[] code = Prepare.EvmCode.PushData(0x01).Op(Instruction.STOP).Done;
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, null, UInt256.Zero, 100_000, code));
        }

        for (int t = 0; t < 5; t++)
            txs.Add(BuildEip1559Tx(senderKey, ref nonce, SimpleContractAddress, UInt256.Zero, 50_000));

        return (txs, nonce);
    }

    private static Transaction BuildEip1559Tx(PrivateKey key, ref int nonce, Address? to, UInt256 value, long gasLimit, byte[]? data = null)
    {
        TransactionBuilder<Transaction> builder = Build.A.Transaction
            .WithType(TxType.EIP1559).WithNonce((UInt256)nonce++).WithTo(to).WithValue(value)
            .WithGasLimit(gasLimit).WithMaxFeePerGas(20.GWei).WithMaxPriorityFeePerGas(1.GWei);
        if (data is not null) builder = builder.WithData(data);
        return builder.SignedAndResolved(key).TestObject;
    }

    // -- RocksDB helpers ------------------------------------------------------

    private static void FlushAndCompactChain(BenchmarkMergeBlockchain chain)
    {
        IDbProvider dbProvider = chain.Container.Resolve<IDbProvider>();
        BenchmarkEnvironmentModule.FlushAndCompact(dbProvider);
    }

    private static void DisableAutoCompactionOnChain(BenchmarkMergeBlockchain chain)
    {
        IDbProvider dbProvider = chain.Container.Resolve<IDbProvider>();
        BenchmarkEnvironmentModule.DisableAutoCompaction(dbProvider);
    }

    // -- Directory helpers ----------------------------------------------------

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(source, dest));
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(source, dest), true);
    }

    private static void TryDeleteDirectory(string? path)
    {
        if (path is null) return;
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort cleanup
        }
    }

    // -- Helper types ---------------------------------------------------------

    private sealed class BenchmarkMergeBlockchain : BaseEngineModuleTests.MergeTestBlockchain
    {
        protected override ChainSpec CreateChainSpec() =>
            new() { Genesis = Core.Test.Builders.Build.A.Block.WithDifficulty(0).WithGasLimit(60_000_000).TestObject };

        protected override AutoCancelTokenSource CreateCancellationSource() =>
            AutoCancelTokenSource.ThatCancelAfter(TimeSpan.FromMinutes(2));

        public Task<BenchmarkMergeBlockchain> BuildChain(Action<ContainerBuilder>? configurer) =>
            BuildMergeTestBlockchain(configurer ?? (_ => { })).ContinueWith(t => (BenchmarkMergeBlockchain)t.Result);
    }
}
