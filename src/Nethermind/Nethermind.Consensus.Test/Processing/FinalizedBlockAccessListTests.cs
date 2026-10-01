// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.History;
using Nethermind.Int256;
using Nethermind.Init.Modules;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Consensus.Test.Processing;

[Parallelizable(ParallelScope.All)]
public class FinalizedBlockAccessListTests
{
    [Test]
    public void Finality_requires_ancestry_and_excludes_the_unfinalized_tail()
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock();
        BlockHeader finalized = Build.A.BlockHeader.WithParent(block.Header).WithBlockAccessListHash(TestItem.KeccakB).TestObject;
        env.Tree.Insert(finalized);
        env.Beacon.GetFinalizedHash().Returns(finalized.Hash);
        BlockHeader sibling = Build.A.BlockHeader.WithParent(env.Genesis.Header).WithDifficulty(0).WithBlockAccessListHash(TestItem.KeccakB).TestObject;
        sibling.IsPostMerge = true;
        BlockHeader tip = Build.A.BlockHeader.WithParent(finalized).WithDifficulty(0).WithBlockAccessListHash(TestItem.KeccakB).TestObject;
        tip.IsPostMerge = true;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Policy.CanReconstruct(block.Header), Is.True);
            Assert.That(env.Policy.CanReconstruct(sibling), Is.False);
            Assert.That(env.Policy.CanReconstruct(tip), Is.False);
        }
    }

    [Test]
    public void Retries_finalized_header_lookup_when_it_arrives_later()
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock(insert: false);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        Assert.That(env.Policy.CanReconstruct(block.Header), Is.False);
        env.Tree.Insert(block.Header);
        Assert.That(env.Policy.CanReconstruct(block.Header), Is.True);
    }

    [Test]
    public void Retention_controls_receipt_requirements([Values] bool store, [Values] bool pruned, [Values] bool retained)
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock();
        env.ReceiptConfig.StoreReceipts = store;
        env.History.CutoffBlockNumber.Returns(pruned ? 2UL : (ulong?)null);
        env.Retention.ShouldRetainReceipts(block.Header).Returns(retained);
        Assert.That(env.Policy.NeedsReceipts(block.Header), Is.EqualTo(store && (!pruned || retained)));
    }

    [Test]
    public void Reconstructs_state_only_when_both_commitments_match([Values] bool matchingRoot, [Values] bool matchingList)
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock(matchingRoot: matchingRoot, matchingList: matchingList);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        bool reconstructed = matchingRoot && matchingList;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(reconstructed ? 0 : 1));
            Assert.That(env.State.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)(reconstructed ? 25 : 100)));
            Assert.That(env.State.StateRoot, Is.EqualTo(reconstructed ? block.StateRoot : env.Genesis.StateRoot));
        }
        block.DisposeAccountChanges();
    }

    [TestCase(ProcessingOptions.ReadOnlyChain)]
    [TestCase(ProcessingOptions.ForceProcessing)]
    [TestCase(ProcessingOptions.NoValidation)]
    [TestCase(ProcessingOptions.EthereumMerge)]
    public void Replay_production_and_engine_validation_execute(ProcessingOptions options)
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, options, NullBlockTracer.Instance, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    [Test]
    public void Missing_required_receipts_fall_back_without_mutating_state()
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock(withTransaction: true);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.ReceiptConfig.StoreReceipts = true;
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(1));
            Assert.That(env.State.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)100));
        }
    }

    [Test]
    public void Downloaded_receipts_are_verified_before_reconstruction([Values] bool validReceipts)
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock(withTransaction: true);
        TxReceipt[] receipts = [new TxReceipt { StatusCode = 1, GasUsedTotal = 21000, Bloom = Bloom.Empty, Logs = [] }];
        block.Header.ReceiptsRoot = ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, Amsterdam.Instance, null);
        block.Header.Hash = block.Header.CalculateHash();
        env.Tree.Insert(block.Header);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.ReceiptConfig.StoreReceipts = true;
        if (!validReceipts) receipts[0].StatusCode = 0;
        env.Receipts.Insert(block, receipts, ensureCanonical: false);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        (Block _, TxReceipt[] result) = env.Processor.ProcessOne(block, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(validReceipts ? 0 : 1));
            Assert.That(result, Has.Length.EqualTo(validReceipts ? 1 : 0));
            Assert.That(env.State.StateRoot, Is.EqualTo(validReceipts ? block.StateRoot : env.Genesis.StateRoot));
        }
        block.DisposeAccountChanges();
    }

    [Test]
    public void Default_configuration_keeps_normal_execution()
    {
        using TestEnvironment env = new(enabled: false);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    [Test]
    public void Unfinalized_blocks_and_traced_blocks_execute([Values] bool tracing)
    {
        using TestEnvironment env = new();
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(tracing ? block.Hash : env.Genesis.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        IBlockTracer tracer = tracing ? Substitute.For<IBlockTracer>() : NullBlockTracer.Instance;
        env.Processor.ProcessOne(block, ProcessingOptions.None, tracer, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    private sealed class TestEnvironment : IDisposable
    {
        private readonly IContainer _container;
        public IBeaconSyncStrategy Beacon { get; } = Substitute.For<IBeaconSyncStrategy>();
        public IHistoryPruner History { get; } = Substitute.For<IHistoryPruner>();
        public IPrunedReceiptRetention Retention { get; } = Substitute.For<IPrunedReceiptRetention>();
        public ReceiptConfig ReceiptConfig { get; } = new() { StoreReceipts = false, DeferredPersistence = false };
        public IBlockTree Tree { get; }
        public IWorldState State { get; }
        public IReceiptStorage Receipts { get; }
        public IBlockProcessor Processor { get; }
        public RecordingProcessor Recording { get; }
        public FinalizedBlockAccessListPolicy Policy { get; }
        public Block Genesis { get; }

        public TestEnvironment(bool enabled = true)
        {
            SyncConfig sync = new() { ReconstructFinalizedStateFromBlockAccessLists = enabled };
            Beacon.MergeTransitionFinished.Returns(true);
            _container = new ContainerBuilder().AddModule(new TestNethermindModule(Amsterdam.Instance))
                .AddSingleton<ISyncConfig>(sync)
                .AddSingleton<IReceiptConfig>(ReceiptConfig)
                .AddSingleton<IBeaconSyncStrategy>(Beacon)
                .AddSingleton<IHistoryPruner>(History)
                .AddSingleton<IPrunedReceiptRetention>(Retention)
                .AddSingleton<FinalizedBlockAccessListPolicy>()
                .AddSingleton<IMainProcessingModule>(new TestProcessingModule(sync))
                .Build();
            MainProcessingContext main = _container.Resolve<MainProcessingContext>();
            State = main.WorldState;
            Processor = main.BlockProcessor;
            Recording = main.LifetimeScope.Resolve<RecordingProcessor>();
            Tree = _container.Resolve<IBlockTree>();
            Receipts = _container.Resolve<IReceiptStorage>();
            Policy = _container.Resolve<FinalizedBlockAccessListPolicy>();
            using (State.BeginScope(IWorldState.PreGenesis))
            {
                State.CreateAccount(TestItem.AddressA, 100);
                State.Commit(Amsterdam.Instance);
                State.RecalculateStateRoot();
                Genesis = Build.A.Block.Genesis.WithStateRoot(State.StateRoot).TestObject;
                State.CommitTree(0);
            }
            Tree.SuggestBlock(Genesis, BlockTreeSuggestOptions.None);
        }

        public Block CreateBlock(bool matchingRoot = true, bool matchingList = true, bool insert = true, bool withTransaction = false)
        {
            Hash256 root;
            using (State.BeginScope(Genesis.Header))
            {
                State.SubtractFromBalance(TestItem.AddressA, 75, Amsterdam.Instance, out _);
                State.Commit(Amsterdam.Instance);
                State.RecalculateStateRoot();
                root = State.StateRoot;
            }
            ReadOnlyBlockAccessList list = new([
                new ReadOnlyAccountChanges(TestItem.AddressA, [], [], [new BalanceChange(1, 25)], [], [])], 0);
            byte[] encoded = Rlp.Encode(list).Bytes;
            BlockBuilder builder = Build.A.Block.WithParent(Genesis).WithDifficulty(0).WithStateRoot(matchingRoot ? root : TestItem.KeccakC)
                .WithBlockAccessListHash(matchingList ? Keccak.Compute(encoded) : TestItem.KeccakD);
            if (withTransaction) builder.WithTransactions(Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject);
            Block block = builder.TestObject;
            block.Header.IsPostMerge = true;
            block.EncodedBlockAccessList = encoded;
            if (insert) Tree.Insert(block.Header);
            return block;
        }

        public void Dispose() => _container.Dispose();
    }

    private sealed class TestProcessingModule(ISyncConfig config) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder) => builder
            .AddScoped<RecordingProcessor>().Bind<IBlockProcessor, RecordingProcessor>()
            .AddModule(new FinalizedBlockAccessListModule(config));
    }

    public sealed class RecordingProcessor : IBlockProcessor
    {
        public int Calls { get; private set; }
        public event Action TransactionsExecuted { add { } remove { } }
        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block block, ProcessingOptions options,
            IBlockTracer tracer, IReleaseSpec spec, CancellationToken token = default)
        {
            Calls++;
            return (block, []);
        }
    }
}
