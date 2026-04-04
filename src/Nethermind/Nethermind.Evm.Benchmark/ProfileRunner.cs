// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Standalone runner for profiling FlatState CV issues with dotnet-trace.
// Usage: dotnet run -c Release -- --profile [erc20|swap|contractcall] [--trie|--flat] [--bench]

#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Db.Rocks;
using Nethermind.Db.Rocks.Config;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.ScopeProvider;
using Nethermind.Trie.Pruning;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Minimal runner for profiling FlatState CV spikes with dotnet-trace.
/// Uses real RocksDB to match production behavior.
/// </summary>
public static class ProfileRunner
{
    public static void Run(string[] args)
    {
        string scenario = "erc20";
        bool useFlatState = false;
        bool benchMode = false;
        for (int a = 1; a < args.Length; a++)
        {
            switch (args[a])
            {
                case "erc20": scenario = "erc20"; break;
                case "swap": scenario = "swap"; break;
                case "contractcall": scenario = "contractcall"; break;
                case "--flat": useFlatState = true; break;
                case "--trie": useFlatState = false; break;
                case "--bench": benchMode = true; break;
            }
        }

        IReleaseSpec spec = Osaka.Instance;
        PrivateKey senderKey = TestItem.PrivateKeyA;
        Address sender = senderKey.Address;
        Address erc20Address = Address.FromNumber(0x1000);
        Address swapAddress = Address.FromNumber(0x2000);
        byte[] stopCode = [0x00];

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;

        Transaction[] txs = scenario switch
        {
            "swap" => BuildSwapTxs(senderKey, swapAddress),
            "contractcall" => BuildContractCallTxs(senderKey, TestItem.AddressB),
            _ => BuildErc20Txs(senderKey, erc20Address),
        };

        Block block = Build.A.Block.WithHeader(header).WithTransactions(txs).TestObject;

        // Build DI container
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        // Create RocksDB-backed IDbProvider
        BenchmarkEnvironmentModule benchModule = new();
        InitConfig initConfig = new() { BaseDbPath = benchModule.BasePath };
        IContainer dbContainer = new ContainerBuilder()
            .AddModule(new DbModule(initConfig, new ReceiptConfig(), new SyncConfig()))
            .AddSingleton<IInitConfig>(initConfig)
            .AddSingleton<IDbConfig>(new DbConfig { SharedBlockCacheSize = 256UL * 1024 * 1024 })
            .AddSingleton<IPruningConfig>(new PruningConfig())
            .AddSingleton<IHardwareInfo>(new TestHardwareInfo())
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<IDbProvider, Nethermind.Core.Test.Db.ContainerOwningDbProvider>()
            .Build();
        IDbProvider dbProvider = dbContainer.Resolve<IDbProvider>();
        IDbFactory dbFactory = dbContainer.Resolve<IDbFactory>();

        IWorldStateManager wsm;
        BlockProcessingBenchmark.BenchmarkFlatDbManager? flatDbManagerRef = null;
        if (useFlatState)
        {
            IColumnsDb<FlatDbColumns> flatColumnsDb = dbFactory.CreateColumnsDb<FlatDbColumns>(
                new DbSettings(DbNames.Flat, DbNames.Flat));
            RocksDbPersistence flatPersistence = new(flatColumnsDb);

            FlatDbConfig flatDbConfig = new() { TrieWarmerWorkerCount = Math.Max(Environment.ProcessorCount - 1, 1) };
            ResourcePool resourcePool = new(flatDbConfig);
            TrieNodeCache trieNodeCache = new(flatDbConfig, LimboLogs.Instance);
            var flatDbManager = new BlockProcessingBenchmark.BenchmarkFlatDbManager(resourcePool, trieNodeCache, flatPersistence);
            flatDbManagerRef = flatDbManager;
            var exitSource = new BlockProcessingBenchmark.BenchmarkProcessExitSource();
            TrieWarmer trieWarmer = new(exitSource, LimboLogs.Instance, flatDbConfig);
            FlatScopeProvider flatScopeProvider = new(
                dbProvider.CodeDb, flatDbManager, flatDbConfig, trieWarmer,
                ResourcePool.Usage.MainBlockProcessing, LimboLogs.Instance, isReadOnly: false);
            wsm = new BlockProcessingBenchmark.BenchmarkFlatWorldStateManager(flatScopeProvider, flatDbManager, dbProvider.CodeDb);
        }
        else
        {
            wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        }

        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;
        IBlockValidationModule[] validationModules = container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = container.Resolve<IMainProcessingModule[]>();
        ILifetimeScope processingScope = container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState stateProvider = processingScope.Resolve<IWorldState>();
        BlockHeader parentHeader;

        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(sender, 1_000_000.Ether);
            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressB, Prepare.EvmCode.PushData(0x01).Op(Instruction.STOP).Done, spec);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, stopCode, spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, stopCode, spec);

            // ERC20
            stateProvider.CreateAccount(erc20Address, UInt256.Zero);
            stateProvider.InsertCode(erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), spec);
            UInt256 senderBalanceSlot = StorageBenchmarkContracts.ComputeMappingSlot(sender, UInt256.Zero);
            byte[] senderBalance = new byte[32]; ((UInt256)1_000_000).ToBigEndian(senderBalance);
            stateProvider.Set(new StorageCell(erc20Address, senderBalanceSlot), senderBalance);
            byte[] recipientBal = new byte[32]; ((UInt256)100).ToBigEndian(recipientBal);
            for (int i = 0; i < 100; i++)
                stateProvider.Set(new StorageCell(erc20Address, StorageBenchmarkContracts.ComputeMappingSlot(Address.FromNumber((UInt256)(100 + i)), UInt256.Zero)), recipientBal);

            // Swap
            UInt256 swapErc20Slot = StorageBenchmarkContracts.ComputeMappingSlot(swapAddress, UInt256.Zero);
            byte[] swapErc20Bal = new byte[32]; ((UInt256)1_000_000_000).ToBigEndian(swapErc20Bal);
            stateProvider.Set(new StorageCell(erc20Address, swapErc20Slot), swapErc20Bal);
            stateProvider.CreateAccount(swapAddress, UInt256.Zero);
            stateProvider.InsertCode(swapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(erc20Address), spec);
            SeedSlot(stateProvider, swapAddress, 0, 1_000_000_000); SeedSlot(stateProvider, swapAddress, 1, 1_000_000_000);
            SeedSlot(stateProvider, swapAddress, 2, 500_000); SeedSlot(stateProvider, swapAddress, 3, 30);
            SeedSlot(stateProvider, swapAddress, 4, 1); SeedSlot(stateProvider, swapAddress, 5, 1);
            SeedSlot(stateProvider, swapAddress, 6, 1); SeedSlot(stateProvider, swapAddress, 7, 1_000_000_000);
            UInt256 senderSwapSlot = StorageBenchmarkContracts.ComputeMappingSlot(sender, (UInt256)8);
            byte[] swapBal = new byte[32]; ((UInt256)1_000).ToBigEndian(swapBal);
            stateProvider.Set(new StorageCell(swapAddress, senderSwapSlot), swapBal);

            stateProvider.Commit(spec);
            stateProvider.CommitTree(0);

            parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        flatDbManagerRef?.Freeze();
        BenchmarkEnvironmentModule.FlushAndCompact(dbProvider);

        IBranchProcessor branchProcessor = processingScope.Resolve<IBranchProcessor>();
        string backendName = useFlatState ? "FlatState" : "Trie";

        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);

        if (benchMode)
            RunBench(branchProcessor, parentHeader, block, scenario, backendName);
        else
            RunProfile(branchProcessor, parentHeader, block, scenario, backendName);

        processingScope.Dispose();
        container.Dispose();
        dbProvider.Dispose();
        benchModule.Cleanup();
    }

    private static void RunProfile(IBranchProcessor branchProcessor, BlockHeader parentHeader, Block block, string scenario, string backendName)
    {
        int iterations = 50000;
        Console.Error.WriteLine($"Warming up {scenario} ({backendName})...");
        for (int i = 0; i < 10; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        Console.Error.WriteLine($"PID={Environment.ProcessId} — running {iterations} iterations of {scenario} ({backendName})...");

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        sw.Stop();

        Console.Error.WriteLine($"Done: {iterations} iterations in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds * 1.0 / iterations:F2} ms/iter)");
    }

    private static void RunBench(IBranchProcessor branchProcessor, BlockHeader parentHeader, Block block, string scenario, string backendName)
    {
        const int OpsPerIter = 10;
        const int Rounds = 20;
        const int ItersPerRound = 200;

        Console.Error.WriteLine($"=== Bench: {scenario} ({backendName}) ===");
        Console.Error.WriteLine($"  {Rounds} rounds x {ItersPerRound} iters x {OpsPerIter} ops/iter");

        Console.Error.WriteLine("  Warming up...");
        for (int i = 0; i < 100; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();

        // Track GC collections during measurement
        int gc0Before = GC.CollectionCount(0);
        int gc1Before = GC.CollectionCount(1);
        int gc2Before = GC.CollectionCount(2);
        long allocBefore = GC.GetTotalAllocatedBytes(precise: false);

        double[] roundMs = new double[Rounds];
        for (int r = 0; r < Rounds; r++)
        {
            int gc0Start = GC.CollectionCount(0);
            int gc1Start = GC.CollectionCount(1);
            int gc2Start = GC.CollectionCount(2);
            long allocStart = GC.GetTotalAllocatedBytes(precise: false);

            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < ItersPerRound; i++)
                for (int op = 0; op < OpsPerIter; op++)
                    branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
            sw.Stop();

            int gc0Delta = GC.CollectionCount(0) - gc0Start;
            int gc1Delta = GC.CollectionCount(1) - gc1Start;
            int gc2Delta = GC.CollectionCount(2) - gc2Start;
            long allocDelta = GC.GetTotalAllocatedBytes(precise: false) - allocStart;

            double msPerOp = sw.Elapsed.TotalMilliseconds / (ItersPerRound * OpsPerIter);
            roundMs[r] = msPerOp;
            Console.Error.WriteLine($"  Round {r + 1,2}/{Rounds}: {msPerOp:F3} ms/op  ({sw.ElapsedMilliseconds} ms total)  GC0={gc0Delta} GC1={gc1Delta} GC2={gc2Delta} alloc={allocDelta / 1024 / 1024}MB");
        }

        int gc0Total = GC.CollectionCount(0) - gc0Before;
        int gc1Total = GC.CollectionCount(1) - gc1Before;
        int gc2Total = GC.CollectionCount(2) - gc2Before;
        long allocTotal = GC.GetTotalAllocatedBytes(precise: false) - allocBefore;
        Console.Error.WriteLine($"\n  Total GC: Gen0={gc0Total} Gen1={gc1Total} Gen2={gc2Total} TotalAlloc={allocTotal / 1024 / 1024}MB");

        // Dump GC heap info to understand what survives to Gen1/Gen2
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        var gcInfo = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        Console.Error.WriteLine($"\n  Heap after full GC:");
        Console.Error.WriteLine($"    HeapSize: {gcInfo.HeapSizeBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"    Promoted: {gcInfo.PromotedBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"    Fragmented: {gcInfo.FragmentedBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"    FinalizationPending: {gcInfo.FinalizationPendingCount}");
        Console.Error.WriteLine($"    Pinned: {gcInfo.PinnedObjectsCount}");
        Console.Error.WriteLine($"    Gen0 size: {gcInfo.GenerationInfo[0].SizeAfterBytes / 1024}KB");
        Console.Error.WriteLine($"    Gen1 size: {gcInfo.GenerationInfo[1].SizeAfterBytes / 1024}KB");
        Console.Error.WriteLine($"    Gen2 size: {gcInfo.GenerationInfo[2].SizeAfterBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"    LOH size: {gcInfo.GenerationInfo[3].SizeAfterBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"    POH size: {gcInfo.GenerationInfo[4].SizeAfterBytes / 1024}KB");

        // Dump finalizer queue types — what objects have finalizers?
        // Analyze what's on the LOH by checking allocation sizes during one round
        Console.Error.WriteLine($"\n  Running allocation analysis round...");
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long heapBefore = GC.GetTotalMemory(false);
        long gen0Before = GC.CollectionCount(0);
        long gen1Before2 = GC.CollectionCount(1);
        long gen2Before2 = GC.CollectionCount(2);

        // Run a single round to measure allocation
        for (int i = 0; i < ItersPerRound; i++)
            for (int op = 0; op < OpsPerIter; op++)
                branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        long heapAfter = GC.GetTotalMemory(false);
        Console.Error.WriteLine($"  Heap growth during round: {(heapAfter - heapBefore) / 1024 / 1024}MB (before GC)");
        Console.Error.WriteLine($"  GC during round: Gen0={GC.CollectionCount(0) - gen0Before} Gen1={GC.CollectionCount(1) - gen1Before2} Gen2={GC.CollectionCount(2) - gen2Before2}");

        // Force full GC and check what survived
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        long heapAfterGC = GC.GetTotalMemory(false);
        var gcInfo2 = GC.GetGCMemoryInfo(GCKind.FullBlocking);
        Console.Error.WriteLine($"  Heap after full GC: {heapAfterGC / 1024 / 1024}MB");
        Console.Error.WriteLine($"  Still pending finalization: {gcInfo2.FinalizationPendingCount}");
        Console.Error.WriteLine($"  Gen2 size: {gcInfo2.GenerationInfo[2].SizeAfterBytes / 1024 / 1024}MB");
        Console.Error.WriteLine($"  LOH size: {gcInfo2.GenerationInfo[3].SizeAfterBytes / 1024 / 1024}MB");

        double mean = roundMs.Average();
        double stdev = Math.Sqrt(roundMs.Select(x => (x - mean) * (x - mean)).Average());
        double cv = mean > 0 ? stdev / mean * 100 : 0;
        Array.Sort(roundMs);
        double median = roundMs[Rounds / 2];

        Console.Error.WriteLine($"\n  {scenario}({backendName}): mean={mean:F3} median={median:F3} stdev={stdev:F3} CV={cv:F2}%");
        Console.WriteLine($"RESULT|{scenario}|{backendName}|{mean:F3}|{median:F3}|{stdev:F3}|{cv:F2}");
    }

    // Uses BenchmarkFlatDbManager, BenchmarkFlatWorldStateManager, BenchmarkProcessExitSource
    // from BlockProcessingBenchmark (internal, same assembly)

    private static Transaction[] BuildErc20Txs(PrivateKey senderKey, Address erc20Address)
    {
        var txs = new Transaction[200];
        for (int i = 0; i < 200; i++)
        {
            byte[] calldata = new byte[64];
            Address.FromNumber((UInt256)(100 + i)).Bytes.CopyTo(calldata.AsSpan(12));
            ((UInt256)1).ToBigEndian(calldata.AsSpan(32));
            txs[i] = Build.A.Transaction.WithNonce((UInt256)i).WithTo(erc20Address).WithData(calldata).WithGasLimit(100_000).WithGasPrice(2.GWei).SignedAndResolved(senderKey).TestObject;
        }
        return txs;
    }

    private static Transaction[] BuildSwapTxs(PrivateKey senderKey, Address swapAddress)
    {
        var txs = new Transaction[200];
        for (int i = 0; i < 200; i++)
        {
            byte[] calldata = new byte[32]; ((UInt256)(i + 1)).ToBigEndian(calldata);
            txs[i] = Build.A.Transaction.WithNonce((UInt256)i).WithTo(swapAddress).WithData(calldata).WithGasLimit(200_000).WithGasPrice(2.GWei).SignedAndResolved(senderKey).TestObject;
        }
        return txs;
    }

    private static Transaction[] BuildContractCallTxs(PrivateKey senderKey, Address contractAddress)
    {
        var txs = new Transaction[200];
        for (int i = 0; i < 200; i++)
            txs[i] = Build.A.Transaction.WithNonce((UInt256)i).WithTo(contractAddress).WithGasLimit(50_000).WithGasPrice(2.GWei).SignedAndResolved(senderKey).TestObject;
        return txs;
    }

    private static void SeedSlot(IWorldState ws, Address addr, UInt256 slot, UInt256 value)
    {
        byte[] bytes = new byte[32]; value.ToBigEndian(bytes);
        ws.Set(new StorageCell(addr, slot), bytes);
    }
}
