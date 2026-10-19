// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark;

/// <summary>Processes Amsterdam blocks that carry their block access list, the shape a validating node imports.</summary>
/// <remarks>
/// <see cref="BlockProcessingBenchmark"/> runs Amsterdam blocks without a supplied list, so it measures the producer
/// path: sequential execution plus the encode and hash of the generated list. Here each block's list is generated once
/// in setup and attached, so <see cref="BlockAccessListManager"/> takes the validator path: transactions run in
/// parallel on the shared processor pool and every slice is checked against the supplied list. The process is not
/// pinned to one core, because that path is what the cores are for. Setup refuses to run when a block falls back to
/// sequential execution or a transaction fails.
/// </remarks>
[Config(typeof(BalBlockValidationConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class BalBlockValidationBenchmark
{
    private const long BlockGasLimit = 200_000_000;
    private const int MaxTxCount = 512;

    private class BalBlockValidationConfig : ManualConfig
    {
        public BalBlockValidationConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithLaunchCount(2)
                .WithWarmupCount(3)
                .WithIterationCount(10)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
            AddColumn(StatisticColumn.P90);
            AddColumn(StatisticColumn.P95);
        }
    }

    /// <summary>Transactions per block; the larger count runs well past one processor per core.</summary>
    [Params(64, MaxTxCount)]
    public int TxCount { get; set; }

    private static readonly IReleaseSpec Spec = Amsterdam.Instance;

    private static readonly byte[] CallTargetCode = Prepare.EvmCode
        .PushData(0x01)
        .Op(Instruction.STOP)
        .Done;

    /// <summary>Writes 2 to the slot named by the first calldata word.</summary>
    private static readonly byte[] SlotWriterCode = Prepare.EvmCode
        .PushData(2)
        .PushData(0)
        .Op(Instruction.CALLDATALOAD)
        .Op(Instruction.SSTORE)
        .Op(Instruction.STOP)
        .Done;

    private static readonly Address TransferTargetAddress = new("0x00000000000000000000000000000000000000ba");
    private static readonly Address CallTargetAddress = new("0x00000000000000000000000000000000000000bb");
    private static readonly Address SlotWriterAddress = new("0x00000000000000000000000000000000000000bc");

    /// <summary>Senders the transactions rotate through, so the list spreads balance and nonce changes over
    /// several accounts, as a mainnet block does.</summary>
    private const int SenderCount = 16;

    private readonly PrivateKey[] _senderKeys = TestItem.PrivateKeys[..SenderCount];

    private IContainer _container = null!;
    private ILifetimeScope _processingScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private BlockHeader _parentHeader = null!;

    private Block _transfersBlock = null!;
    private Block _contractCallsBlock = null!;
    private Block _slotWritesBlock = null!;

    /// <summary>Seeds the parent state and builds each block with the access list its own execution generates.</summary>
    [GlobalSetup]
    public void GlobalSetup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .Build();

        _processingScope = BlockProcessingBenchmarkSetup.BeginProcessingScope(_container, out TestStateHeaderProvider stateHeaderProvider);

        IWorldState stateProvider = _processingScope.Resolve<IWorldState>();
        using (stateProvider.BeginScope(IWorldState.PreGenesis))
        {
            foreach (PrivateKey senderKey in _senderKeys)
            {
                stateProvider.CreateAccount(senderKey.Address, 1_000_000.Ether);
            }

            stateProvider.CreateAccount(TransferTargetAddress, UInt256.One);

            stateProvider.CreateAccount(CallTargetAddress, UInt256.Zero);
            stateProvider.InsertCode(CallTargetAddress, CallTargetCode, Spec);

            // Seeded slots, so each write updates an existing slot instead of paying the new-slot state charge.
            stateProvider.CreateAccount(SlotWriterAddress, UInt256.Zero);
            stateProvider.InsertCode(SlotWriterAddress, SlotWriterCode, Spec);
            for (int slot = 0; slot < MaxTxCount; slot++)
            {
                stateProvider.Set(new StorageCell(SlotWriterAddress, (UInt256)slot), UInt256.One);
            }

            BlockProcessingBenchmarkSetup.SeedSystemContracts(stateProvider, Spec);

            stateProvider.Commit(Spec);
            stateProvider.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(stateProvider.StateRoot)
                .WithGasLimit(BlockGasLimit)
                .TestObject;
            stateHeaderProvider.Parent = _parentHeader;
        }

        _branchProcessor = _processingScope.Resolve<IBranchProcessor>();

        _transfersBlock = WithGeneratedAccessList(BuildTransactions(i => Build.A.Transaction
            .WithTo(TransferTargetAddress)
            .WithValue(1.Wei)
            .WithGasLimit(50_000)));
        _contractCallsBlock = WithGeneratedAccessList(BuildTransactions(i => Build.A.Transaction
            .WithTo(CallTargetAddress)
            .WithGasLimit(100_000)));
        _slotWritesBlock = WithGeneratedAccessList(BuildTransactions(i => Build.A.Transaction
            .WithTo(SlotWriterAddress)
            .WithData(((UInt256)i).ToBigEndian())
            .WithGasLimit(200_000)));
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _processingScope.Dispose();
        _container.Dispose();
    }

    /// <summary>Value transfers to one seeded account.</summary>
    [Benchmark]
    public Block[] Transfers() => Process(_transfersBlock);

    /// <summary>Calls to a contract that only pushes and stops.</summary>
    [Benchmark]
    public Block[] ContractCalls() => Process(_contractCallsBlock);

    /// <summary>Each transaction updates its own seeded storage slot of one contract.</summary>
    [Benchmark]
    public Block[] SlotWrites() => Process(_slotWritesBlock);

    private Block[] Process(Block block) =>
        _branchProcessor.Process(_parentHeader, [block], ProcessingOptions.NoValidation, NullBlockTracer.Instance);

    private Transaction[] BuildTransactions(Func<int, TransactionBuilder<Transaction>> shape)
    {
        Transaction[] transactions = new Transaction[TxCount];
        for (int i = 0; i < transactions.Length; i++)
        {
            transactions[i] = shape(i)
                .WithNonce((ulong)(i / SenderCount))
                .WithGasPrice(2.GWei)
                .SignedAndResolved(_senderKeys[i % SenderCount])
                .TestObject;
        }

        return transactions;
    }

    /// <summary>Runs the block once without a list, then returns it carrying the list that run generated.</summary>
    private Block WithGeneratedAccessList(Transaction[] transactions)
    {
        BlockHeader header = Build.A.BlockHeader
            .WithNumber(1)
            .WithParentHash(_parentHeader.Hash!)
            .WithGasLimit(BlockGasLimit)
            .WithBaseFee(1.GWei)
            .WithTimestamp(1)
            .TestObject;
        Block producerShape = Build.A.Block.WithHeader(header).WithTransactions(transactions).TestObject;

        BlockReceiptsTracer tracer = new();
        Block generated = _branchProcessor.Process(_parentHeader, [producerShape], ProcessingOptions.NoValidation, tracer)[0];
        BlockProcessingBenchmarkSetup.EnsureAllSucceeded(tracer, $"with {TxCount} transactions");

        Block validatorShape = new(generated.Header.Clone(), generated.Body,
            Rlp.Decode<ReadOnlyBlockAccessList>(generated.EncodedBlockAccessList!));

        // A traced block runs sequentially, so this pass runs untraced, as the benchmark does. A list that does not
        // match the execution throws here.
        Process(validatorShape);
        if (!_processingScope.Resolve<IBlockAccessListManager>().ParallelExecutionEnabled)
        {
            throw new InvalidOperationException("The block fell back to sequential execution - the benchmark would not measure the validator path.");
        }

        return validatorShape;
    }
}
