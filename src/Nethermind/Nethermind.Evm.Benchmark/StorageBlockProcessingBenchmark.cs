// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Block processing of storage-shaped blocks through <see cref="BranchProcessor.Process"/>, as the live client
/// processes them.
/// </summary>
/// <remarks>
/// Kept apart from <see cref="BlockProcessingBenchmark"/> because its state seeds thousands of accounts and slots,
/// which would change the trie every scenario there reads.
/// <para>
/// A world state is reused from block to block, so its per-transaction maps keep the capacity of the largest
/// transaction it has run. <see cref="TokenTransfers_AfterLargeTxs"/> processes a block of two such transactions once
/// in its setup and then measures the same small transactions that <see cref="TokenTransfers"/> measures on a state
/// that never ran one, so the pair shows what the small transactions pay once the maps have grown.
/// </para>
/// </remarks>
[Config(typeof(StorageBlockProcessingConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class StorageBlockProcessingBenchmark
{
    private const int BlocksPerInvoke = 20;

    private class StorageBlockProcessingConfig : ManualConfig
    {
        public StorageBlockProcessingConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithLaunchCount(2)
                .WithWarmupCount(2)
                .WithIterationCount(5)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    [Params("Osaka", "Amsterdam")]
    public string Fork { get; set; } = "Osaka";

    private IReleaseSpec Spec => Fork switch
    {
        "Osaka" => Osaka.Instance,
        "Amsterdam" => Amsterdam.Instance,
        _ => throw new ArgumentOutOfRangeException(nameof(Fork), Fork, "Unmapped fork - BDN would silently label a wrong spec."),
    };

    /// <summary>Contracts that each read and rewrite their own <see cref="SlotsPerStorageContract"/> slots.</summary>
    private const int StorageContracts = 32;
    private const int SlotsPerStorageContract = 8;

    /// <summary>Each router calls <see cref="StorageContractsPerRouter"/> different storage contracts.</summary>
    private const int Routers = StorageContracts / StorageContractsPerRouter;
    private const int StorageContractsPerRouter = 8;
    private const int StorageHeavyTxs = 24;

    /// <summary>Token-like contracts that move one unit between two slots, as a token transfer does.</summary>
    private const int TokenContracts = 16;
    private const int TokenTxs = 100;

    /// <summary>Slots one transaction rewrites, and accounts another one reads, to grow the per-transaction maps.</summary>
    private const int LargeTxSlots = 1_000;
    private const int LargeTxAccounts = 3_000;

    private static readonly Address LargeStorageTxTarget = At(0xc300);
    private static readonly Address LargeAccountTxTarget = At(0xc301);

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private BlockHeader _parentHeader = null!;
    private BlockHeader _header = null!;

    private Block _storageHeavyBlock = null!;
    private Block _tokenTransfersBlock = null!;
    private Block _largeTxsBlock = null!;

    private static Address At(int number)
    {
        byte[] bytes = new byte[Address.Size];
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(Address.Size - sizeof(int)), number);
        return new Address(bytes);
    }

    private static Address StorageContract(int index) => At(0xc000 + index);
    private static Address Router(int index) => At(0xc100 + index);
    private static Address TokenContract(int index) => At(0xc200 + index);
    private static Address SeededAccount(int index) => At(0x10000 + index);

    /// <summary>Adds one to a slot, so every write is a non-zero to non-zero reset priced alike on both forks.</summary>
    private static Prepare Increment(Prepare code, int slot) => code
        .PushData(slot)
        .Op(Instruction.SLOAD)
        .PushData(1)
        .Op(Instruction.ADD)
        .PushData(slot)
        .Op(Instruction.SSTORE);

    private static byte[] BuildStorageContractCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int slot = 0; slot < SlotsPerStorageContract; slot++) code = Increment(code, slot);
        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildRouterCode(int router)
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < StorageContractsPerRouter; i++)
        {
            code = code.Call(StorageContract(router * StorageContractsPerRouter + i), 200_000).Op(Instruction.POP);
        }

        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildTokenCode() => Increment(Increment(Prepare.EvmCode, 0), 1).Op(Instruction.STOP).Done;

    private static byte[] BuildLargeStorageTxCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int slot = 0; slot < LargeTxSlots; slot++) code = Increment(code, slot);
        return code.Op(Instruction.STOP).Done;
    }

    private static byte[] BuildLargeAccountTxCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < LargeTxAccounts; i++)
        {
            code = code.PushData(SeededAccount(i)).Op(Instruction.BALANCE).Op(Instruction.POP);
        }

        return code.Op(Instruction.STOP).Done;
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        {
            Process.GetCurrentProcess().ProcessorAffinity = new IntPtr(1);
        }

        _header = Build.A.BlockHeader
            .WithNumber(1)
            .WithGasLimit(30_000_000)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;

        ulong nonce = 0;
        Transaction[] storageHeavyTxs = new Transaction[StorageHeavyTxs];
        for (int i = 0; i < storageHeavyTxs.Length; i++)
        {
            storageHeavyTxs[i] = BuildCall(Router(i % Routers), nonce++, 1_500_000);
        }

        _storageHeavyBlock = BuildBlock(storageHeavyTxs);

        nonce = 0;
        _tokenTransfersBlock = BuildBlock(BuildTokenTransfers(ref nonce));

        nonce = 0;
        _largeTxsBlock = BuildBlock([BuildCall(LargeStorageTxTarget, nonce++, 14_000_000), BuildCall(LargeAccountTxTarget, nonce++, 10_000_000)]);

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .Build();
        _processingScope = BlockProcessingBenchmarkSetup.BeginProcessingScope(_container, out TestStateHeaderProvider stateHeaderProvider);

        IWorldState state = _processingScope.Resolve<IWorldState>();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(_senderKey.Address, 1_000_000.Ether);
            SeedState(state);
            BlockProcessingBenchmarkSetup.SeedSystemContracts(state, Spec);

            state.Commit(Spec);
            state.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(state.StateRoot)
                .WithGasLimit(30_000_000)
                .TestObject;
            stateHeaderProvider.Parent = _parentHeader;
            _header.ParentHash = _parentHeader.Hash;
            _header.Hash = _header.CalculateHash();
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();

        foreach (Block block in (Block[])[_storageHeavyBlock, _tokenTransfersBlock])
        {
            ProcessVerified(block);
        }
    }

    /// <summary>The common setup, then one run of the large transactions, whose map capacity outlives their block.</summary>
    [GlobalSetup(Target = nameof(TokenTransfers_AfterLargeTxs))]
    public void GlobalSetupAfterLargeTxs()
    {
        GlobalSetup();
        ProcessVerified(_largeTxsBlock);
    }

    private void ProcessVerified(Block block)
    {
        BlockReceiptsTracer tracer = new();
        _branchProcessor.Process(_parentHeader, [block], ProcessingOptions.NoValidation, tracer);
        BlockProcessingBenchmarkSetup.EnsureAllSucceeded(tracer, $"under {Fork}");
    }

    private void SeedState(IWorldState state)
    {
        byte[] storageContractCode = BuildStorageContractCode();
        for (int i = 0; i < StorageContracts; i++)
        {
            Address contract = StorageContract(i);
            state.CreateAccount(contract, UInt256.Zero);
            state.InsertCode(contract, storageContractCode, Spec);
            for (int slot = 0; slot < SlotsPerStorageContract; slot++) state.Set(new StorageCell(contract, (UInt256)slot), UInt256.One);
        }

        for (int i = 0; i < Routers; i++)
        {
            state.CreateAccount(Router(i), UInt256.Zero);
            state.InsertCode(Router(i), BuildRouterCode(i), Spec);
        }

        byte[] tokenCode = BuildTokenCode();
        for (int i = 0; i < TokenContracts; i++)
        {
            Address token = TokenContract(i);
            state.CreateAccount(token, UInt256.Zero);
            state.InsertCode(token, tokenCode, Spec);
            state.Set(new StorageCell(token, UInt256.Zero), UInt256.One);
            state.Set(new StorageCell(token, UInt256.One), UInt256.One);
        }

        state.CreateAccount(LargeStorageTxTarget, UInt256.Zero);
        state.InsertCode(LargeStorageTxTarget, BuildLargeStorageTxCode(), Spec);
        for (int slot = 0; slot < LargeTxSlots; slot++) state.Set(new StorageCell(LargeStorageTxTarget, (UInt256)slot), UInt256.One);

        state.CreateAccount(LargeAccountTxTarget, UInt256.Zero);
        state.InsertCode(LargeAccountTxTarget, BuildLargeAccountTxCode(), Spec);
        for (int i = 0; i < LargeTxAccounts; i++) state.CreateAccount(SeededAccount(i), UInt256.One);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    /// <summary>Calls through routers into many contracts, each reading and rewriting its own slots.</summary>
    [Benchmark(OperationsPerInvoke = BlocksPerInvoke)]
    public Block[] StorageHeavy_ManyAccounts() => ProcessRepeatedly(_storageHeavyBlock);

    /// <summary>Small transactions that each rewrite two slots of one of several contracts.</summary>
    [Benchmark(OperationsPerInvoke = BlocksPerInvoke)]
    public Block[] TokenTransfers() => ProcessRepeatedly(_tokenTransfersBlock);

    /// <summary>The <see cref="TokenTransfers"/> block after a block with one transaction that rewrote many slots and one that read many accounts.</summary>
    [Benchmark(OperationsPerInvoke = BlocksPerInvoke)]
    public Block[] TokenTransfers_AfterLargeTxs() => ProcessRepeatedly(_tokenTransfersBlock);

    private Block[] ProcessRepeatedly(Block block)
    {
        Block[] result = null!;
        for (int i = 0; i < BlocksPerInvoke; i++)
            result = _branchProcessor.Process(_parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);
        return result;
    }

    private Transaction[] BuildTokenTransfers(ref ulong nonce)
    {
        Transaction[] txs = new Transaction[TokenTxs];
        for (int i = 0; i < txs.Length; i++)
        {
            txs[i] = BuildCall(TokenContract(i % TokenContracts), nonce++, 100_000);
        }

        return txs;
    }

    private Transaction BuildCall(Address to, ulong nonce, ulong gasLimit)
        => Build.A.Transaction
            .WithNonce(nonce)
            .WithTo(to)
            .WithGasLimit(gasLimit)
            .WithGasPrice(2.GWei)
            .SignedAndResolved(_senderKey)
            .TestObject;

    private Block BuildBlock(Transaction[] transactions)
        => Build.A.Block
            .WithHeader(_header)
            .WithTransactions(transactions)
            .TestObject;
}
