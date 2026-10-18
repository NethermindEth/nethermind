// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Autofac;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.NoEmit;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Init.Modules;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization;
using MergeMetrics = Nethermind.Merge.Plugin.Metrics;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// Catches up a segment of finalized Amsterdam blocks from the same pre-state either by applying each block's
/// EIP-7928 access list (<see cref="FinalizedBlockAccessListProcessor"/>) or by executing it, as a node that was
/// offline does with <c>Sync.ReconstructFinalizedStateFromBlockAccessLists</c> on or off.
/// </summary>
/// <remarks>
/// The segment is produced once in <see cref="GlobalSetup"/>: each block is executed on the producer path, and its
/// header takes the produced roots and access-list hash, so the segment is a valid chain. As forward sync does, the
/// encoded lists go to the access-list store and are attached by <see cref="BlockAccessListRecoveryStep"/>, the
/// receipts go to the receipt store, and the headers go to the block tree under a finalized tip.
/// <para>Both arms process the whole segment through <see cref="IBranchProcessor"/> with
/// <see cref="ProcessingOptions.StoreReceipts"/>; the off arm flips the runtime flag the policy reads, so execution
/// takes the regular validator path (parallel when enabled) with the list attached. One operation is one block, so
/// blocks/s is 1 / Mean; gas/s is printed by setup as gas per block.</para>
/// <para>Setup refuses to benchmark if the on arm falls back for any block or the off arm reconstructs any.</para>
/// </remarks>
[Config(typeof(Config))]
[MemoryDiagnoser]
public class BalCatchUpBenchmark
{
    private const int Blocks = 64;
    private const ulong BlockGasLimit = 100_000_000;
    private const int SlotsPerWrite = 10;
    private const int HashesPerCompute = 1_500;

    private class Config : ManualConfig
    {
        public Config()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithInvocationCount(1)
                .WithUnrollFactor(1)
                .WithWarmupCount(2)
                .WithIterationCount(10)
                .WithGcForce(true));
            AddColumn(StatisticColumn.Min, StatisticColumn.Median, StatisticColumn.P95);
        }
    }

    /// <summary>The shape of every block in the segment.</summary>
    /// <remarks><c>DevnetLike</c> approximates a glamsterdam-devnet-8 block (about 6 Mgas over 66 transactions);
    /// <c>StorageHeavy</c> maximises written items per gas, the access list's worst case; <c>ComputeHeavy</c> spends
    /// gas without touching state, its best case.</remarks>
    [Params("DevnetLike", "StorageHeavy", "ComputeHeavy")]
    public string Shape { get; set; } = "DevnetLike";

    /// <summary>Whether finalized blocks are caught up from their access lists.</summary>
    [Params(true, false)]
    public bool BalCatchUp { get; set; }

    /// <summary>Whether state is held in the flat database, the client default, or the trie database.</summary>
    [Params(true, false)]
    public bool FlatDb { get; set; }

    private static readonly Address WriterAddress = new("0x00000000000000000000000000000000000000b1");
    private static readonly Address HasherAddress = new("0x00000000000000000000000000000000000000b2");
    private static readonly byte[] StopCode = [0x00];

    private readonly PrivateKey _senderKey = TestItem.PrivateKeyA;
    private readonly SyncConfig _syncConfig = new() { ReconstructFinalizedStateFromBlockAccessLists = true };
    private readonly FinalizedBeacon _beacon = new();
    private IContainer _container = null!;
    private IBranchProcessor _branch = null!;
    private BlockHeader _genesis = null!;
    private Block[] _segment = [];
    private ulong _nonce;
    private int _recipient;

    [GlobalSetup]
    public void GlobalSetup()
    {
        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = FlatDb }))
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .AddSingleton<ISyncConfig>(_syncConfig)
            .AddSingleton<IReceiptConfig>(new ReceiptConfig { StoreReceipts = true, DeferredPersistence = false })
            .AddSingleton<IBeaconSyncStrategy>(_beacon)
            .AddSingleton<FinalizedBlockAccessListPolicy>()
            .AddSingleton<IMainProcessingModule>(ctx => new FinalizedBlockAccessListModule(_syncConfig, ctx.Resolve<ISpecProvider>()))
            .Build();

        MainProcessingContext main = _container.Resolve<MainProcessingContext>();
        IWorldState state = main.WorldState;
        _branch = main.BranchProcessor;
        IBlockTree tree = _container.Resolve<IBlockTree>();

        _genesis = CreateGenesis(state, tree);
        _segment = ProduceSegment(tree);

        IBlockAccessListStore lists = _container.Resolve<IBlockAccessListStore>();
        BlockAccessListRecoveryStep recovery = _container.Resolve<IEnumerable<IBlockPreprocessorStep>>().OfType<BlockAccessListRecoveryStep>().Single();
        foreach (Block block in _segment)
        {
            lists.Insert(block.Number, block.Hash!, block.EncodedBlockAccessList!);
            block.EncodedBlockAccessList = null;
            block.GeneratedBlockAccessList = null;
            recovery.RecoverDataForQueuedProcessing(block);
            if (block.BlockAccessList is null) throw new InvalidOperationException($"No access list attached to block {block.Number}.");
        }

        _beacon.Finalized = _segment[^1].Hash;
        _syncConfig.ReconstructFinalizedStateFromBlockAccessLists = BalCatchUp;

        long balBefore = MergeMetrics.BalCatchUpBlocks;
        long fallbacksBefore = Fallbacks();
        CatchUp();
        long reconstructed = MergeMetrics.BalCatchUpBlocks - balBefore;
        long fallbacks = Fallbacks() - fallbacksBefore;
        if (reconstructed != (BalCatchUp ? Blocks : 0) || fallbacks != 0)
            throw new InvalidOperationException($"BalCatchUp={BalCatchUp}: {reconstructed} blocks reconstructed and {fallbacks} fell back, so the arm does not measure the path it names.");

        ulong gas = _segment.Aggregate(0UL, (sum, b) => sum + b.GasUsed);
        int txs = _segment.Sum(b => b.Transactions.Length);
        int items = _segment.Sum(b => b.BlockAccessList!.ItemCount);
        Console.WriteLine($"// {Shape}: {Blocks} blocks, {gas / Blocks:N0} gas/block, {txs / Blocks} txs/block, {items / Blocks} BAL items/block");
    }

    private static long Fallbacks()
    {
        long sum = 0;
        foreach (KeyValuePair<BalCatchUpFallbackReason, long> entry in MergeMetrics.BalCatchUpFallbacks) sum += entry.Value;
        return sum;
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        foreach (Block block in _segment) block.DisposeAccountChanges();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _container.Dispose();

    [Benchmark(OperationsPerInvoke = Blocks)]
    public Block[] CatchUp() =>
        _branch.Process(_genesis, _segment, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);

    private BlockHeader CreateGenesis(IWorldState state, IBlockTree tree)
    {
        IReleaseSpec spec = Amsterdam.Instance;
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.CreateAccount(_senderKey.Address, 1_000_000_000.Ether);
            state.CreateAccount(WriterAddress, UInt256.Zero);
            state.InsertCode(WriterAddress, BuildWriterCode(), spec);
            state.CreateAccount(HasherAddress, UInt256.Zero);
            state.InsertCode(HasherAddress, BuildHasherCode(), spec);
            for (int i = 0; i < 4_096; i++) state.CreateAccount(Recipient(i), UInt256.One);
            foreach (Address predeploy in (Address[])[
                Eip7002Constants.WithdrawalRequestPredeployAddress, Eip7251Constants.ConsolidationRequestPredeployAddress,
                Eip8282Constants.BuilderDepositRequestPredeployAddress, Eip8282Constants.BuilderExitRequestPredeployAddress])
            {
                state.CreateAccount(predeploy, UInt256.Zero);
                state.InsertCode(predeploy, StopCode, spec);
            }

            state.Commit(spec);
            state.RecalculateStateRoot();
            Block genesis = Build.A.Block.Genesis.WithStateRoot(state.StateRoot).WithGasLimit(BlockGasLimit).TestObject;
            state.CommitTree(0);
            tree.SuggestBlock(genesis, BlockTreeSuggestOptions.None);
            return genesis.Header;
        }
    }

    private Block[] ProduceSegment(IBlockTree tree)
    {
        Block[] segment = new Block[Blocks];
        BlockHeader parent = _genesis;
        for (int i = 0; i < Blocks; i++)
        {
            Block suggested = Build.A.Block.WithParent(parent).WithDifficulty(0).WithGasLimit(BlockGasLimit)
                .WithBaseFeePerGas(1.GWei).WithTimestamp(parent.Timestamp + 12).WithTransactions(BuildTransactions()).TestObject;
            suggested.Header.IsPostMerge = true;

            BlockReceiptsTracer tracer = new();
            Block produced = _branch.Process(parent, [suggested], ProcessingOptions.NoValidation, tracer)[0];
            foreach (TxReceipt receipt in tracer.TxReceipts)
            {
                if (receipt.StatusCode != StatusCode.Success)
                    throw new InvalidOperationException($"{Shape} transaction failed in block {produced.Number} ({receipt.Error}).");
            }

            BlockHeader header = produced.Header;
            header.Hash = header.CalculateHash();
            Block block = new(header, produced.Body) { EncodedBlockAccessList = produced.EncodedBlockAccessList };
            produced.DisposeAccountChanges();
            tree.Insert(header);
            _container.Resolve<IReceiptStorage>().Insert(block, tracer.TxReceipts.ToArray(), ensureCanonical: false);
            segment[i] = block;
            parent = header;
        }

        return segment;
    }

    private Transaction[] BuildTransactions() => Shape switch
    {
        "DevnetLike" => [.. Transfers(50), .. Calls(WriterAddress, 6), .. Calls(HasherAddress, 10)],
        "StorageHeavy" => [.. Calls(WriterAddress, 90)],
        "ComputeHeavy" => [.. Calls(HasherAddress, 200)],
        _ => throw new ArgumentOutOfRangeException(nameof(Shape), Shape, null),
    };

    private IEnumerable<Transaction> Transfers(int count)
    {
        for (int i = 0; i < count; i++)
            yield return Sign(Build.A.Transaction.WithTo(Recipient(_recipient++ % 4_096)).WithValue(1.Wei).WithGasLimit(21_000));
    }

    private IEnumerable<Transaction> Calls(Address target, int count)
    {
        for (int i = 0; i < count; i++)
            yield return Sign(Build.A.Transaction.WithTo(target).WithGasLimit(target == WriterAddress ? 2_000_000UL : 150_000UL));
    }

    private Transaction Sign(TransactionBuilder<Transaction> builder) =>
        builder.WithType(TxType.EIP1559).WithNonce(_nonce++).WithMaxFeePerGas(2.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .SignedAndResolved(_senderKey).TestObject;

    private static Address Recipient(int i) => new(Keccak.Compute(i.ToBigEndianByteArray()).Bytes[..20]);

    /// <summary>Writes <see cref="SlotsPerWrite"/> fresh slots after the counter in slot 0, then advances it.</summary>
    private static byte[] BuildWriterCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 1; i <= SlotsPerWrite; i++)
            code.PushData(1).PushData(0).Op(Instruction.SLOAD).PushData(i).Op(Instruction.ADD).Op(Instruction.SSTORE);
        return code.PushData(SlotsPerWrite).PushData(0).Op(Instruction.SLOAD).Op(Instruction.ADD).PushData(0).Op(Instruction.SSTORE)
            .Op(Instruction.STOP).Done;
    }

    /// <summary>Hashes a memory word <see cref="HashesPerCompute"/> times without touching state.</summary>
    private static byte[] BuildHasherCode()
    {
        Prepare code = Prepare.EvmCode;
        for (int i = 0; i < HashesPerCompute; i++)
            code.PushData(32).PushData(0).Op(Instruction.KECCAK256).PushData(0).Op(Instruction.MSTORE);
        return code.Op(Instruction.STOP).Done;
    }

    private sealed class FinalizedBeacon : IBeaconSyncStrategy
    {
        public Hash256? Finalized { get; set; }
        public bool ShouldBeInBeaconHeaders() => false;
        public bool ShouldBeInBeaconModeControl() => false;
        public bool IsBeaconSyncFinished(BlockHeader? blockHeader) => true;
        public bool MergeTransitionFinished => true;
        public ulong? GetTargetBlockHeight() => null;
        public Hash256? GetFinalizedHash() => Finalized;
        public Hash256? GetHeadBlockHash() => Finalized;
    }
}
