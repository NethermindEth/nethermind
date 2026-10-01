// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

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
using Nethermind.Db;
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

[TestFixture(false)]
[TestFixture(true)]
[Parallelizable(ParallelScope.All)]
public class FinalizedBlockAccessListTests(bool useFlatDb)
{
    [Test]
    public void Finality_requires_ancestry_and_excludes_the_unfinalized_tail()
    {
        using TestEnvironment env = new(useFlatDb);
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
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(insert: false);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        Assert.That(env.Policy.CanReconstruct(block.Header), Is.False);
        env.Tree.Insert(block.Header);
        Assert.That(env.Policy.CanReconstruct(block.Header), Is.True);
    }

    [Test]
    public void Retention_controls_receipt_requirements([Values] bool store, [Values] bool pruned, [Values] bool retained)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock();
        env.ReceiptConfig.StoreReceipts = store;
        env.History.CutoffBlockNumber.Returns(pruned ? 2UL : (ulong?)null);
        env.Retention.ShouldRetainReceipts(block.Header).Returns(retained);
        Assert.That(env.Policy.NeedsReceipts(block.Header), Is.EqualTo(store && (!pruned || retained)));
    }

    [Test]
    public void Reconstructs_state_only_when_both_commitments_match([Values] bool matchingRoot, [Values] bool matchingList)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(matchingRoot: matchingRoot, matchingList: matchingList);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.None, NullBlockTracer.Instance);
        using IDisposable scope = env.State.BeginScope(block.Header);
        bool reconstructed = matchingRoot && matchingList;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(reconstructed ? 0 : 1));
            Assert.That(env.Recording.LastOptions.HasFlag(ProcessingOptions.ForceSequentialBlockAccessList),
                Is.EqualTo(matchingList && !matchingRoot));
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
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, options, NullBlockTracer.Instance, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    [Test]
    public void Missing_required_receipts_fall_back_without_mutating_state()
    {
        using TestEnvironment env = new(useFlatDb);
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
        using TestEnvironment env = new(useFlatDb);
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
        using TestEnvironment env = new(useFlatDb, enabled: false);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    [Test]
    public void Unfinalized_blocks_and_traced_blocks_execute([Values] bool tracing)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(tracing ? block.Hash : env.Genesis.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        IBlockTracer tracer = tracing ? Substitute.For<IBlockTracer>() : NullBlockTracer.Instance;
        env.Processor.ProcessOne(block, ProcessingOptions.None, tracer, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(1));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void Consecutive_finalized_blocks_reconstruct_through_branch_processing(bool mismatchSecond)
    {
        using TestEnvironment env = new(useFlatDb);
        Block first = env.CreateBlock();
        Block second = env.CreateBlock(parent: first, balance: 15, matchingRoot: !mismatchSecond);
        Block third = env.CreateBlock(parent: second, balance: 5);
        env.Beacon.GetFinalizedHash().Returns(third.Hash);
        Block[] result = env.Branch.Process(env.Genesis.Header, [first, second, third], ProcessingOptions.None, NullBlockTracer.Instance);
        using IDisposable scope = env.State.BeginScope(third.Header);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Has.Length.EqualTo(3));
            Assert.That(env.Recording.Calls, Is.EqualTo(mismatchSecond ? 1 : 0));
            Assert.That(env.Recording.LastOptions.HasFlag(ProcessingOptions.ForceSequentialBlockAccessList), Is.EqualTo(mismatchSecond));
            if (mismatchSecond) Assert.That(env.Recording.BalanceAtEntry, Is.EqualTo((UInt256)25));
            Assert.That(env.State.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)5));
            Assert.That(env.State.StateRoot, Is.EqualTo(third.StateRoot));
        }
        foreach (Block block in result) block.DisposeAccountChanges();
    }

    [Test]
    public void Finality_advances_without_rewalking_cached_ancestors()
    {
        using TestEnvironment env = new(useFlatDb);
        Block first = env.CreateBlock();
        Block second = env.CreateBlock(parent: first);
        Block third = env.CreateBlock(parent: second);
        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.FindHeader(first.Hash!, BlockTreeLookupOptions.None).Returns(first.Header);
        tree.FindHeader(second.Hash!, BlockTreeLookupOptions.None).Returns(second.Header);
        tree.FindHeader(third.Hash!, BlockTreeLookupOptions.None).Returns(third.Header);
        tree.FindHeader(env.Genesis.Hash!, BlockTreeLookupOptions.None).Returns(env.Genesis.Header);
        FinalizedBlockAccessListPolicy policy = new(new SyncConfig { ReconstructFinalizedStateFromBlockAccessLists = true },
            env.Beacon, tree, new Nethermind.Specs.TestSpecProvider(Amsterdam.Instance), env.ReceiptConfig, () => env.History, env.Retention);
        env.Beacon.GetFinalizedHash().Returns(second.Hash);
        Assert.That(policy.CanReconstruct(first.Header), Is.True);
        tree.ClearReceivedCalls();
        env.Beacon.GetFinalizedHash().Returns(third.Hash);
        Assert.That(policy.CanReconstruct(first.Header), Is.True);
        Assert.That(policy.CanReconstruct(second.Header), Is.True);
        tree.Head.Returns(first);
        Assert.That(policy.CanReconstruct(first.Header), Is.False);
        Assert.That(policy.CanReconstruct(second.Header), Is.True);
        tree.DidNotReceive().FindHeader(env.Genesis.Hash!, BlockTreeLookupOptions.None);
        tree.DidNotReceive().FindHeader(first.Hash!, BlockTreeLookupOptions.None);
        tree.Received(1).FindHeader(second.Hash!, BlockTreeLookupOptions.None);
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
        public IBranchProcessor Branch { get; }
        public RecordingProcessor Recording { get; }
        public FinalizedBlockAccessListPolicy Policy { get; }
        public Block Genesis { get; }

        public TestEnvironment(bool useFlatDb, bool enabled = true)
        {
            SyncConfig sync = new() { ReconstructFinalizedStateFromBlockAccessLists = enabled };
            Beacon.MergeTransitionFinished.Returns(true);
            _container = new ContainerBuilder().AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = useFlatDb }))
                .AddSingleton<ISpecProvider>(new Nethermind.Specs.TestSpecProvider(Amsterdam.Instance))
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
            Branch = main.BranchProcessor;
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

        public Block CreateBlock(bool matchingRoot = true, bool matchingList = true, bool insert = true, bool withTransaction = false, Block? parent = null, UInt256? balance = null)
        {
            parent ??= Genesis;
            UInt256 finalBalance = balance ?? 25;
            Hash256 root;
            using (State.BeginScope(Genesis.Header))
            {
                State.SubtractFromBalance(TestItem.AddressA, 100 - finalBalance, Amsterdam.Instance, out _);
                State.Commit(Amsterdam.Instance);
                State.RecalculateStateRoot();
                root = State.StateRoot;
            }
            ReadOnlyBlockAccessList list = new([
                new ReadOnlyAccountChanges(TestItem.AddressA, [], [], [new BalanceChange(1, finalBalance)], [], [])], 0);
            byte[] encoded = Rlp.Encode(list).Bytes;
            BlockBuilder builder = Build.A.Block.WithParent(parent).WithDifficulty(0).WithStateRoot(matchingRoot ? root : TestItem.KeccakC)
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

    public sealed class RecordingProcessor(IWorldState state) : IBlockProcessor
    {
        public int Calls { get; private set; }
        public ProcessingOptions LastOptions { get; private set; }
        public UInt256 BalanceAtEntry { get; private set; }
        public event Action? TransactionsExecuted { add { } remove { } }
        public (Block Block, TxReceipt[] Receipts) ProcessOne(Block block, ProcessingOptions options,
            IBlockTracer tracer, IReleaseSpec spec, CancellationToken token = default)
        {
            Calls++;
            LastOptions = options;
            BalanceAtEntry = state.GetBalance(TestItem.AddressA);
            block.Header.StateRoot = state.StateRoot;
            return (block, []);
        }
    }
}
