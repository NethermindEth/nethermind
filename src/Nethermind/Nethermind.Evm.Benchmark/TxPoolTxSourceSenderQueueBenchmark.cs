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
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using Nethermind.State;
using Nethermind.TxPool;
using Nethermind.TxPool.Comparison;
using NSubstitute;

namespace Nethermind.Evm.Benchmark;

/// <summary>
/// The first build of a block from a pool in which one sender queues a long run of transactions with high gas limits:
/// how many of its later nonces <see cref="TxPoolTxSource"/> hands out, and what the
/// <see cref="BlockProcessor.BlockProductionTransactionPicker"/> does with those that no longer fit.
/// </summary>
/// <remarks>
/// Three layers, all on the source the producer uses (the standard filter pipeline and producer comparer):
/// <list type="bullet">
/// <item><see cref="EnumerateSource"/>: the source alone, over the candidates the build walks.</item>
/// <item><see cref="SelectTransactions"/>: the source and the picker's decisions alone, without execution. An added
/// candidate bumps its sender's nonce and the block's gas by what it used in <see cref="GlobalSetup"/>'s build, so the
/// time is the heap, the filters and the picker checks, including those of the candidates the picker skips.</item>
/// <item><see cref="ProduceBlock"/>: the producer's first build with real execution on in-memory state, through
/// <see cref="IBranchProcessor"/> with <see cref="ProcessingOptions.ProducingBlock"/> and the production transactions
/// executor, as <see cref="OneTimeChainProcessor"/> runs it.</item>
/// </list>
/// <para>Before each case <see cref="GlobalSetup"/> prints, from one untimed build of each layer, how many candidates
/// the source handed out, how many the block included and why the picker skipped the others; it refuses to run a
/// case whose two layers disagree.</para>
/// <para>Every build is a first build: the pool's transactions have not run, so they carry no gas used from an earlier
/// build. The pool reports itself revalidated for the target spec, its usual state, so the source does not re-check
/// fork-sensitive validity per candidate.</para>
/// </remarks>
[Config(typeof(SenderQueueConfig))]
[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class TxPoolTxSourceSenderQueueBenchmark
{
    private class SenderQueueConfig : ManualConfig
    {
        public SenderQueueConfig()
        {
            AddJob(Job.Default
                .WithToolchain(InProcessNoEmitToolchain.Instance)
                .WithUnrollFactor(1)
                .WithLaunchCount(1)
                .WithWarmupCount(3)
                .WithIterationCount(15));
            AddColumn(StatisticColumn.Min);
            AddColumn(StatisticColumn.Max);
            AddColumn(StatisticColumn.Median);
        }
    }

    public enum PoolShape
    {
        /// <summary>One sender's transfers: each uses 21,000 gas under a far higher gas limit.</summary>
        CheapTransfers,

        /// <summary>One sender's calls that use all but <see cref="BurnerGasLeft"/> of their gas limit, so the block
        /// fills while more than the base cost of a transaction is still free.</summary>
        GasBurners,

        /// <summary>Half the pool is one sender's gas burners, the other half ordinary senders' few plain
        /// transfers, with fees above, equal to and below the long sender's.</summary>
        Mixed,
    }

    [ParamsAllValues]
    public PoolShape Shape { get; set; }

    /// <summary>Today's mainnet gas limit (the <c>TargetBlockGasLimit</c> of the mainnet config) and a higher one.</summary>
    [Params(60_000_000UL, 200_000_000UL)]
    public ulong BlockGasLimit { get; set; }

    /// <summary>The default pool size (<c>TxPoolConfig.Size</c>): with no per-sender cap by default
    /// (<c>MaxPendingTxsPerSender</c> = 0) it is the longest queue one sender can have.</summary>
    private const int PoolSize = 2048;

    /// <summary>A gas limit far above what the queued transactions use, as a generous fixed limit or an
    /// over-estimate would set.</summary>
    private const ulong QueuedGasLimit = 1_000_000;

    private const ulong TransferGasLimit = 21_000;

    private const int OrdinarySenders = 256;

    private const int OrdinaryTransactionsPerSender = (PoolSize / 2) / OrdinarySenders;

    /// <summary>The burner stops once no more than this is left, so a call uses all but about this much of its gas
    /// limit: a block of them is full to within this much per transaction, which stays above the base cost of a
    /// transaction at both block gas limits.</summary>
    private const int BurnerGasLeft = 1_000;

    private static readonly IReleaseSpec Spec = Osaka.Instance;

    private static readonly Address TransferTargetAddress = new("0x00000000000000000000000000000000000000ba");

    private static readonly Address BurnerAddress = new("0x00000000000000000000000000000000000000b0");

    /// <summary>Loops while more than <see cref="BurnerGasLeft"/> gas is left, then stops.</summary>
    private static readonly byte[] BurnerCode = Prepare.EvmCode
        .Op(Instruction.JUMPDEST)
        .PushData(BurnerGasLeft)
        .Op(Instruction.GAS)
        .Op(Instruction.GT)
        .PushData(0)
        .Op(Instruction.JUMPI)
        .Op(Instruction.STOP)
        .Done;

    private IContainer _container = null!;
    private ILifetimeScope _productionScope = null!;
    private IBranchProcessor _branchProcessor = null!;
    private IWorldState _worldState = null!;
    private BlockProcessor.IBlockProductionTransactionPicker _picker = null!;
    private TxPoolTxSource _txSource = null!;
    private BlockHeader _parentHeader = null!;
    private BlockHeader _headerTemplate = null!;
    private Transaction[] _pooledTransactions = null!;
    private ulong _transferGasUsed;
    private ulong _burnerGasUsed;
    private int _candidatesVisited;

    [GlobalSetup]
    public void GlobalSetup()
    {
        Dictionary<AddressAsKey, Transaction[]> pending = BuildPool();
        _pooledTransactions = pending.Values.SelectMany(static bucket => bucket).ToArray();

        _container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(Spec))
            .Build();
        _productionScope = BlockProcessingBenchmarkSetup.BeginProductionScope(_container, out TestStateHeaderProvider stateHeaderProvider);
        _worldState = _productionScope.Resolve<IWorldState>();

        using (_worldState.BeginScope(IWorldState.PreGenesis))
        {
            foreach (AddressAsKey sender in pending.Keys)
            {
                _worldState.CreateAccount(sender, 1_000_000.Ether);
            }

            _worldState.CreateAccount(TransferTargetAddress, UInt256.One);
            _worldState.CreateAccount(BurnerAddress, UInt256.Zero);
            _worldState.InsertCode(BurnerAddress, BurnerCode, Spec);
            BlockProcessingBenchmarkSetup.SeedSystemContracts(_worldState, Spec);

            _worldState.Commit(Spec);
            _worldState.CommitTree(0);

            _parentHeader = Build.A.BlockHeader
                .WithNumber(0)
                .WithStateRoot(_worldState.StateRoot)
                .WithGasLimit(BlockGasLimit)
                .WithBaseFee(1.GWei)
                .TestObject;
            stateHeaderProvider.Parent = _parentHeader;
        }

        _headerTemplate = Build.A.BlockHeader
            .WithParentHash(_parentHeader.Hash!)
            .WithNumber(1)
            .WithGasLimit(BlockGasLimit)
            .WithBaseFee(BaseFeeCalculator.Calculate(_parentHeader, Spec))
            .WithTimestamp(_parentHeader.Timestamp + 12)
            .TestObject;

        ITxPool txPool = Substitute.For<ITxPool>();
        txPool.GetPendingForProduction(Arg.Any<BlockHeader>(), Arg.Any<bool>(), Arg.Any<UInt256>())
            .Returns(new PendingTransactionsView(pending, new Dictionary<AddressAsKey, Transaction[]>(), isRevalidated: true));
        IBlocksConfig blocksConfig = _container.Resolve<IBlocksConfig>();
        // The container's log manager logs errors only. LimboLogs would build every trace message, one per candidate
        // in the source, which a node at its default level never does.
        ILogManager logManager = _container.Resolve<ILogManager>();
        _txSource = new TxPoolTxSource(
            txPool,
            _container.Resolve<ISpecProvider>(),
            _container.Resolve<ITransactionComparerProvider>(),
            logManager,
            TxFilterPipelineBuilder.CreateStandardFilteringPipeline(logManager, blocksConfig),
            blocksConfig,
            _container.ResolveKeyed<ITxValidator>(ITxValidator.SpecChangeTxValidatorKey));
        _picker = _productionScope.Resolve<BlockProcessor.IBlockProductionTransactionPicker>();
        _branchProcessor = _productionScope.Resolve<IBranchProcessor>();

        VerifyLayersAgree();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _productionScope.Dispose();
        _container.Dispose();
    }

    /// <summary>The source alone, over the candidates <see cref="SelectTransactions"/> walks: the heap and the
    /// filters without the picker.</summary>
    [Benchmark]
    public int EnumerateSource()
    {
        int handedOut = 0;
        foreach (Transaction _ in GetTransactions())
        {
            if (++handedOut == _candidatesVisited) break;
        }

        return handedOut;
    }

    [Benchmark]
    public int SelectTransactions() => Select(skipReasons: null).Included;

    [Benchmark]
    public Block ProduceBlock()
    {
        ResetToFirstBuild();
        return _branchProcessor.Process(_parentHeader, [CreateBlock(GetTransactions())],
            ProcessingOptions.ProducingBlock, NullBlockTracer.Instance)[0];
    }

    /// <summary>Mirrors <see cref="BlockProcessor.BlockProductionTransactionsExecutor"/>'s walk over the candidates,
    /// with execution replaced by its effect on what the picker reads: the sender's nonce and the block's gas.</summary>
    /// <remarks>The block's transaction byte length stays 0 here, so the picker's size check never binds; the pooled
    /// transactions add up to far less than <see cref="BlocksConfig.DefaultMaxTxKilobytes"/> anyway.</remarks>
    private (int Visited, int Included, ulong GasUsed) Select(Dictionary<string, int>? skipReasons)
    {
        BlockToProduce block = CreateBlock([]);
        using IDisposable stateScope = _worldState.BeginScope(_parentHeader);
        HashSet<Transaction> consideredTx = new(ByHashTxComparer.Instance);
        int visited = 0;
        int included = 0;
        ulong gasUsed = 0;
        foreach (Transaction tx in GetTransactions())
        {
            visited++;
            BlockProcessor.AddingTxEventArgs args = _picker.CanAddTransaction(block, tx, consideredTx, _worldState, gasUsed, 0);
            if (args.Action != BlockProcessor.TxAction.Add && skipReasons is not null)
            {
                string reason = args.Reason.Split([',', '-', '('])[0].Trim();
                skipReasons[reason] = skipReasons.GetValueOrDefault(reason) + 1;
            }

            if (args.Action == BlockProcessor.TxAction.Stop) break;

            consideredTx.Add(tx);
            if (args.Action == BlockProcessor.TxAction.Add)
            {
                _worldState.IncrementNonce(tx.SenderAddress!);
                gasUsed += tx.To == BurnerAddress ? _burnerGasUsed : _transferGasUsed;
                included++;
            }
        }

        return (visited, included, gasUsed);
    }

    private IEnumerable<Transaction> GetTransactions() =>
        _txSource.GetTransactions(_parentHeader, _headerTemplate, BlockGasLimit, filterSource: true);

    private BlockToProduce CreateBlock(IEnumerable<Transaction> transactions) =>
        new(_headerTemplate.Clone(), transactions, [], []);

    /// <summary>Forgets the gas used that execution recorded on the pooled transactions, as before a first build.</summary>
    private void ResetToFirstBuild()
    {
        foreach (Transaction tx in _pooledTransactions)
        {
            tx.BlockGasUsed = 0;
        }
    }

    /// <summary>Builds the block once with each layer, prints what each saw and refuses a case they disagree on.</summary>
    /// <remarks>Also takes the gas a transfer and a burner use from the executed build, for the selection layer.</remarks>
    private void VerifyLayersAgree()
    {
        ResetToFirstBuild();
        int handedOut = 0;
        BlockReceiptsTracer tracer = new();
        Block produced = _branchProcessor.Process(_parentHeader, [CreateBlock(CountHandedOut(GetTransactions()))],
            ProcessingOptions.ProducingBlock, tracer)[0];
        BlockProcessingBenchmarkSetup.EnsureAllSucceeded(tracer, $"in {Shape} at {BlockGasLimit:N0}");

        for (int i = 0; i < produced.Transactions.Length; i++)
        {
            ulong gasUsed = tracer.TxReceipts[i].GasUsed;
            if (produced.Transactions[i].To == BurnerAddress) _burnerGasUsed = gasUsed;
            else _transferGasUsed = gasUsed;
        }

        ResetToFirstBuild();
        Dictionary<string, int> skipReasons = [];
        (int visited, int included, ulong selectedGas) = Select(skipReasons);

        Console.WriteLine($"// {nameof(TxPoolTxSourceSenderQueueBenchmark)} {Shape} at block gas limit {BlockGasLimit:N0}, pool {_pooledTransactions.Length}: " +
            $"{nameof(ProduceBlock)} handed out {handedOut} candidates, included {produced.Transactions.Length} using {produced.GasUsed:N0} gas; " +
            $"{nameof(SelectTransactions)} handed out {visited}, included {included} using {selectedGas:N0} gas; picker skips: " +
            (skipReasons.Count == 0 ? "none" : string.Join(", ", skipReasons.Select(static r => $"{r.Key} x{r.Value}"))));

        if (produced.Transactions.Length == 0 || visited != handedOut || included != produced.Transactions.Length || selectedGas != produced.GasUsed)
        {
            throw new InvalidOperationException($"The selection layer does not model the {Shape} build - its numbers would be meaningless.");
        }

        _candidatesVisited = visited;

        IEnumerable<Transaction> CountHandedOut(IEnumerable<Transaction> transactions)
        {
            foreach (Transaction tx in transactions)
            {
                handedOut++;
                yield return tx;
            }
        }
    }

    private Dictionary<AddressAsKey, Transaction[]> BuildPool()
    {
        PrivateKey longSender = TestItem.PrivateKeyA;
        Dictionary<AddressAsKey, Transaction[]> pending = [];
        switch (Shape)
        {
            case PoolShape.CheapTransfers:
                pending[longSender.Address] = BuildQueue(longSender, PoolSize, TransferTargetAddress, QueuedGasLimit, 1.GWei);
                break;
            case PoolShape.GasBurners:
                pending[longSender.Address] = BuildQueue(longSender, PoolSize, BurnerAddress, QueuedGasLimit, 1.GWei);
                break;
            case PoolShape.Mixed:
                pending[longSender.Address] = BuildQueue(longSender, PoolSize / 2, BurnerAddress, QueuedGasLimit, 1.GWei);
                for (int i = 0; i < OrdinarySenders; i++)
                {
                    byte[] keyBytes = new byte[32];
                    keyBytes[0] = 0x0b;
                    keyBytes[30] = (byte)(i >> 8);
                    keyBytes[31] = (byte)i;
                    PrivateKey sender = new(keyBytes);
                    // Priority fees of 0.5, 1 and 1.5 gwei put a third of the ordinary senders ahead of the long one.
                    pending[sender.Address] = BuildQueue(sender, OrdinaryTransactionsPerSender, TransferTargetAddress, TransferGasLimit, (UInt256)(i % 3 + 1) * Unit.GWei / 2);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(Shape), Shape, "Unmapped pool shape - BDN would silently label a wrong pool.");
        }

        return pending;
    }

    private static Transaction[] BuildQueue(PrivateKey sender, int count, Address to, ulong gasLimit, UInt256 priorityFee)
    {
        Transaction[] queue = new Transaction[count];
        for (int nonce = 0; nonce < count; nonce++)
        {
            queue[nonce] = Build.A.Transaction
                .WithType(TxType.EIP1559)
                .WithNonce((ulong)nonce)
                .WithTo(to)
                .WithValue(to == BurnerAddress ? UInt256.Zero : 1.Wei)
                .WithGasLimit(gasLimit)
                .WithMaxFeePerGas(10.GWei)
                .WithMaxPriorityFeePerGas(priorityFee)
                .SignedAndResolved(sender)
                .TestObject;
        }

        return queue;
    }
}
