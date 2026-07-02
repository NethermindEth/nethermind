// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Diagnostics;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Container;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Shared infrastructure for block benchmarks that stress per-transaction
/// warm/cold (EIP-2929) tracking cost: a block of <see cref="TxCount"/> calls to a
/// contract that SLOADs <see cref="SlotsPerTx"/> distinct storage slots, so each tx
/// builds a warm set of that size which is cleared on tx dispose.
/// </summary>
/// <remarks>
/// Mirrors <see cref="BlockProcessingBenchmark"/>: full <see cref="BranchProcessor"/>
/// pipeline (including pre-warming) under <see cref="Osaka"/> rules, one world state
/// built in GlobalSetup, fresh scope per <c>Process</c> call. Slot keys are the same
/// for every tx, so state-cache noise stays flat while the per-tx warm-set size is
/// controlled exactly by <see cref="SlotsPerTx"/>.
/// </remarks>
public abstract class SyntheticStorageBlockBenchmarkBase
{
    protected class SyntheticBlockConfig : ManualConfig
    {
        public SyntheticBlockConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(2)
                .WithIterationCount(8)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
        }
    }

    protected const int OpsPerInvoke = 128;

    private static readonly IReleaseSpec Spec = Osaka.Instance;

    // Minimal bytecode (STOP) for system contract stubs
    private static readonly byte[] StopCode = [0x00];

    // High enough that even 300 txs x ~230k gas fit; block gas limit is not the subject here.
    private const long BlockGasLimit = 1_000_000_000;

    // Per slot: PUSH2 (3) + SLOAD cold (2100) + POP (2)
    private const ulong GasPerSlot = 2105;

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private BlockHeader _parentHeader = null!;
    private Block _block = null!;

    /// <summary>Number of transactions in the benchmarked block.</summary>
    protected abstract int TxCount { get; }

    /// <summary>Distinct storage slots each transaction SLOADs (per-tx warm-set size).</summary>
    protected abstract int SlotsPerTx { get; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        // Pin to a single core to reduce OS scheduler jitter (same as BlockProcessingBenchmark)
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
        }

        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(BlockGasLimit)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;

        _block = Build.A.Block
            .WithHeader(header)
            .WithTransactions(BuildContractCalls(TxCount, SlotsPerTx))
            .TestObject;

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Osaka.Instance))
            .Build();

        IDbProvider dbProvider = TestMemDbProvider.Init();
        IWorldStateManager wsm = TestWorldStateFactory.CreateWorldStateManagerForTest(dbProvider, LimboLogs.Instance);
        IWorldStateScopeProvider scopeProvider = wsm.GlobalWorldState;

        IBlockValidationModule[] validationModules = _container.Resolve<IBlockValidationModule[]>();
        IMainProcessingModule[] mainProcessingModules = _container.Resolve<IMainProcessingModule[]>();
        _processingScope = _container.BeginLifetimeScope(b =>
        {
            b.RegisterInstance(scopeProvider).As<IWorldStateScopeProvider>().ExternallyOwned();
            b.RegisterInstance(wsm).As<IWorldStateManager>().ExternallyOwned();
            b.AddModule(validationModules);
            b.AddModule(mainProcessingModules);
        });

        IWorldState stateProvider = _processingScope.Resolve<IWorldState>();

        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            stateProvider.CreateAccount(_senderKey.Address, 1_000_000.Ether);

            stateProvider.CreateAccount(TestItem.AddressB, UInt256.Zero);
            stateProvider.InsertCode(TestItem.AddressB, BuildSloadCode(SlotsPerTx), Spec);

            stateProvider.CreateAccount(Eip7002Constants.WithdrawalRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7002Constants.WithdrawalRequestPredeployAddress, StopCode, Spec);
            stateProvider.CreateAccount(Eip7251Constants.ConsolidationRequestPredeployAddress, UInt256.Zero);
            stateProvider.InsertCode(Eip7251Constants.ConsolidationRequestPredeployAddress, StopCode, Spec);

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(BlockGasLimit)
                .TestObject;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    protected Block[] ProcessBlockCore()
    {
        Block[] result = null!;
        for (int i = 0; i < OpsPerInvoke; i++)
            result = _branchProcessor.Process(_parentHeader, [_block],
                ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    /// <summary>Straight-line code SLOADing <paramref name="slots"/> distinct keys: (PUSH2 i, SLOAD, POP)* STOP.</summary>
    private static byte[] BuildSloadCode(int slots)
    {
        byte[] code = new byte[slots * 5 + 1];
        int p = 0;
        for (int i = 0; i < slots; i++)
        {
            code[p++] = (byte)Instruction.PUSH2;
            code[p++] = (byte)(i >> 8);
            code[p++] = (byte)i;
            code[p++] = (byte)Instruction.SLOAD;
            code[p++] = (byte)Instruction.POP;
        }
        code[p] = (byte)Instruction.STOP;
        return code;
    }

    private Transaction[] BuildContractCalls(int count, int slots)
    {
        ulong gasLimit = GasTransaction + (ulong)slots * GasPerSlot + GasMargin;
        Transaction[] txs = new Transaction[count];
        for (int i = 0; i < count; i++)
        {
            txs[i] = Build.A.Transaction
                .WithNonce((ulong)i)
                .WithTo(TestItem.AddressB)
                .WithGasLimit(gasLimit)
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKey)
                .TestObject;
        }
        return txs;
    }

    private const ulong GasTransaction = 21_000;
    private const ulong GasMargin = 2_000;
}
