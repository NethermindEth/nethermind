// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Standalone runner for profiling and benchmarking.
// Profiling:  --profile [erc20|swap] [--trie|--flat]
// Benchmarking: --profile [erc20|swap] [--trie|--flat] --bench

#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using Autofac;
using Nethermind.Blockchain.Tracing;
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
using Nethermind.Evm.Benchmark;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
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
/// Minimal runner that executes the storage-heavy benchmarks in a tight loop,
/// suitable for profiling with dotnet-trace.
/// </summary>
public static class ProfileRunner
{
    public static void Run(string[] args)
    {
        // Parse args: --profile [erc20|swap] [--trie|--flat] [--bench]
        string scenario = "erc20";
        bool useFlatState = false;
        bool benchMode = false;
        for (int a = 1; a < args.Length; a++)
        {
            switch (args[a])
            {
                case "erc20": scenario = "erc20"; break;
                case "swap": scenario = "swap"; break;
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
            .WithBaseFee(1.GWei())
            .WithTimestamp(1)
            .TestObject;

        // Build transactions
        Transaction[] txs;
        if (scenario == "swap")
        {
            txs = new Transaction[200];
            for (int i = 0; i < 200; i++)
            {
                byte[] calldata = new byte[32];
                ((UInt256)(i + 1)).ToBigEndian(calldata);
                txs[i] = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithTo(swapAddress)
                    .WithData(calldata)
                    .WithGasLimit(200_000)
                    .WithGasPrice(2.GWei())
                    .SignedAndResolved(senderKey)
                    .TestObject;
            }
        }
        else
        {
            txs = new Transaction[200];
            for (int i = 0; i < 200; i++)
            {
                byte[] calldata = new byte[64];
                Address.FromNumber((UInt256)(100 + i)).Bytes.CopyTo(calldata.AsSpan(12));
                ((UInt256)1).ToBigEndian(calldata.AsSpan(32));
                txs[i] = Build.A.Transaction
                    .WithNonce((UInt256)i)
                    .WithTo(erc20Address)
                    .WithData(calldata)
                    .WithGasLimit(100_000)
                    .WithGasPrice(2.GWei())
                    .SignedAndResolved(senderKey)
                    .TestObject;
            }
        }

        Block block = Build.A.Block
            .WithHeader(header)
            .WithTransactions(txs)
            .TestObject;

        // Build DI container
        IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm;
        BlockProcessingBenchmark.BenchmarkFlatDbManager? flatDbManagerRef = null;
        if (useFlatState)
        {
            FlatDbConfig flatDbConfig = new() { TrieCacheMemoryBudget = 0 };
            ResourcePool resourcePool = new(flatDbConfig);
            TrieNodeCache trieNodeCache = new(flatDbConfig, LimboLogs.Instance);
            BlockProcessingBenchmark.BenchmarkFlatDbManager flatDbManager = new(resourcePool, trieNodeCache);
            flatDbManagerRef = flatDbManager;
            FlatScopeProvider flatScopeProvider = new(
                dbProvider.CodeDb,
                flatDbManager,
                flatDbConfig,
                new NoopTrieWarmer(),
                ResourcePool.Usage.MainBlockProcessing,
                LimboLogs.Instance,
                isReadOnly: false);
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
            stateProvider.CreateAccount(sender, 1_000_000.Ether());
            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, stopCode, spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, stopCode, spec);

            // ERC20 contract
            stateProvider.CreateAccount(erc20Address, UInt256.Zero);
            stateProvider.InsertCode(erc20Address, StorageBenchmarkContracts.BuildErc20RuntimeCode(), spec);

            UInt256 senderBalanceSlot = StorageBenchmarkContracts.ComputeMappingSlot(sender, UInt256.Zero);
            byte[] senderBalance = new byte[32];
            ((UInt256)1_000_000).ToBigEndian(senderBalance);
            stateProvider.Set(new StorageCell(erc20Address, senderBalanceSlot), senderBalance);

            byte[] recipientInitialBalance = new byte[32];
            ((UInt256)100).ToBigEndian(recipientInitialBalance);
            for (int i = 0; i < 100; i++)
            {
                Address recipient = Address.FromNumber((UInt256)(100 + i));
                UInt256 recipientSlot = StorageBenchmarkContracts.ComputeMappingSlot(recipient, UInt256.Zero);
                stateProvider.Set(new StorageCell(erc20Address, recipientSlot), recipientInitialBalance);
            }

            // Swap contract
            stateProvider.CreateAccount(swapAddress, UInt256.Zero);
            stateProvider.InsertCode(swapAddress, StorageBenchmarkContracts.BuildSwapRuntimeCode(), spec);

            void SeedSlot(UInt256 slot, UInt256 value)
            {
                byte[] bytes = new byte[32];
                value.ToBigEndian(bytes);
                stateProvider.Set(new StorageCell(swapAddress, slot), bytes);
            }

            SeedSlot(0, 1_000_000_000);
            SeedSlot(1, 1_000_000_000);
            SeedSlot(2, 500_000);
            SeedSlot(3, 30);
            SeedSlot(4, 1);
            SeedSlot(5, 1);
            SeedSlot(6, 1);
            SeedSlot(7, 1_000_000_000);

            UInt256 senderSwapSlot = StorageBenchmarkContracts.ComputeMappingSlot(sender, (UInt256)8);
            byte[] senderSwapBalance = new byte[32];
            ((UInt256)1_000).ToBigEndian(senderSwapBalance);
            stateProvider.Set(new StorageCell(swapAddress, senderSwapSlot), senderSwapBalance);

            stateProvider.Commit(spec);
            stateProvider.CommitTree(0);

            parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
        }

        // Freeze the flat db manager so iterations don't accumulate snapshots
        flatDbManagerRef?.Freeze();

        IBranchProcessor branchProcessor = processingScope.Resolve<IBranchProcessor>();

        string backendName = useFlatState ? "FlatState" : "Trie";

        // Pin to a single core to reduce OS scheduler jitter
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
        }

        if (benchMode)
        {
            RunBench(branchProcessor, parentHeader, block, scenario, backendName);
        }
        else
        {
            RunProfile(branchProcessor, parentHeader, block, scenario, backendName);
        }

        processingScope.Dispose();
        container.Dispose();
    }

    private static void RunProfile(IBranchProcessor branchProcessor, BlockHeader parentHeader, Block block, string scenario, string backendName)
    {
        int iterations = 500;

        Console.Error.WriteLine($"Warming up {scenario} ({backendName})...");
        for (int i = 0; i < 10; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        Console.Error.WriteLine($"PID={Environment.ProcessId} — running {iterations} iterations of {scenario} ({backendName}). Attach profiler now or wait...");
        Console.Error.WriteLine("Press ENTER to start timed loop (or wait 3s)...");

        // Give dotnet-trace time to attach
        Thread.Sleep(3000);

        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        sw.Stop();

        Console.Error.WriteLine($"Done: {iterations} iterations of {scenario} ({backendName}) in {sw.ElapsedMilliseconds} ms ({sw.ElapsedMilliseconds * 1.0 / iterations:F2} ms/iter)");
    }

    private static void RunBench(IBranchProcessor branchProcessor, BlockHeader parentHeader, Block block, string scenario, string backendName)
    {
        // Each "iteration" processes the block OpsPerIter times, so each
        // timed measurement covers ~40-50 ms of real work, drowning out
        // OS scheduling noise that dominates sub-5 ms measurements.
        const int OpsPerIter = 10;
        const int Rounds = 10;
        const int ItersPerRound = 500;
        int totalOps = Rounds * ItersPerRound * OpsPerIter;

        Console.Error.WriteLine($"=== Bench: {scenario} ({backendName}) ===");
        Console.Error.WriteLine($"  {Rounds} rounds x {ItersPerRound} iters x {OpsPerIter} ops/iter = {totalOps} total block processes");

        // Warmup: 100 block processes
        Console.Error.WriteLine("  Warming up (100 ops)...");
        for (int i = 0; i < 100; i++)
            branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

        // Force GC before measurement
        GC.Collect(2, GCCollectionMode.Forced, true, true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, true, true);

        double[] roundMs = new double[Rounds];

        for (int r = 0; r < Rounds; r++)
        {
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < ItersPerRound; i++)
            {
                for (int op = 0; op < OpsPerIter; op++)
                    branchProcessor.Process(parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
            }
            sw.Stop();

            double msPerOp = sw.Elapsed.TotalMilliseconds / (ItersPerRound * OpsPerIter);
            roundMs[r] = msPerOp;
            Console.Error.WriteLine($"  Round {r + 1,2}/{Rounds}: {msPerOp:F3} ms/op  ({sw.ElapsedMilliseconds} ms total)");
        }

        // Statistics
        double mean = 0;
        for (int i = 0; i < Rounds; i++) mean += roundMs[i];
        mean /= Rounds;

        double variance = 0;
        for (int i = 0; i < Rounds; i++) variance += (roundMs[i] - mean) * (roundMs[i] - mean);
        variance /= Rounds;
        double stdev = Math.Sqrt(variance);
        double cv = mean > 0 ? stdev / mean * 100 : 0;

        // Median
        double[] sorted = new double[Rounds];
        Array.Copy(roundMs, sorted, Rounds);
        Array.Sort(sorted);
        double median = Rounds % 2 == 0
            ? (sorted[Rounds / 2 - 1] + sorted[Rounds / 2]) / 2
            : sorted[Rounds / 2];

        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {scenario}({backendName}): mean={mean:F3} ms/op  median={median:F3} ms/op  stdev={stdev:F3}  CV={cv:F2}%");
        // Machine-readable line for easy parsing
        Console.WriteLine($"RESULT|{scenario}|{backendName}|{mean:F3}|{median:F3}|{stdev:F3}|{cv:F2}");
    }
}
