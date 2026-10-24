// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Config;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Container;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Nethermind.Init.Modules;
using Nethermind.Merge.Plugin.Synchronization;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Synchronization;
using NSubstitute;
using NUnit.Framework;
using MergeMetrics = Nethermind.Merge.Plugin.Metrics;

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
    public void Blocks_with_transactions_need_a_receipt_store([Values] bool storeReceipts, [Values] bool withTransaction)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(withTransaction: withTransaction);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.ReceiptConfig.StoreReceipts = storeReceipts;
        Assert.That(env.Policy.CanReconstruct(block.Header), Is.EqualTo(storeReceipts || !withTransaction));
    }

    [Test]
    public void Reconstructs_state_only_when_both_commitments_match([Values] bool matchingRoot, [Values] bool matchingList)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(matchingRoot: matchingRoot, matchingList: matchingList);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        long missingListsBefore = Fallbacks(BalCatchUpFallbackReason.NoBlockAccessList);
        long rootMismatchesBefore = Fallbacks(BalCatchUpFallbackReason.StateRootMismatch);
        env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.None, NullBlockTracer.Instance);
        using IDisposable scope = env.State.BeginScope(block.Header);
        bool reconstructed = matchingRoot && matchingList;
        using (Assert.EnterMultipleScope())
        {
            // Other tests count fallbacks concurrently, so only an increase is asserted.
            if (!matchingList) Assert.That(Fallbacks(BalCatchUpFallbackReason.NoBlockAccessList), Is.GreaterThan(missingListsBefore));
            if (matchingList && !matchingRoot) Assert.That(Fallbacks(BalCatchUpFallbackReason.StateRootMismatch), Is.GreaterThan(rootMismatchesBefore));
            Assert.That(env.Recording.Calls, Is.EqualTo(reconstructed ? 0 : 1));
            Assert.That(env.Recording.LastOptions.HasFlag(ProcessingOptions.ForceSequentialBlockAccessList),
                Is.EqualTo(matchingList && !matchingRoot));
            Assert.That(env.State.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)(reconstructed ? 25 : 100)));
            Assert.That(env.State.StateRoot, Is.EqualTo(reconstructed ? block.StateRoot : env.Genesis.StateRoot));
            Assert.That(block.AccountChanges?.Contains(TestItem.AddressA) == true, Is.EqualTo(reconstructed), "changed accounts for the tx pool cache");
        }
        block.DisposeAccountChanges();
    }

    [Test]
    public void List_over_the_item_limit_is_executed([Values] bool overLimit)
    {
        using TestEnvironment env = new(useFlatDb);
        // The list holds one account, so a gas limit just under one item's cost allows none.
        Block block = env.CreateBlock(gasLimit: Eip7928Constants.ItemCost - (overLimit ? 1ul : 0ul));
        Assert.That(block.BlockAccessList is null, Is.EqualTo(overLimit), "queued processing leaves a list over the limit off");
        // Attach it regardless, as a path that skipped the queued-processing check would.
        block.BlockAccessList = env.StoredList(block);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        Assert.That(env.Recording.Calls, Is.EqualTo(overLimit ? 1 : 0));
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

    [TestCase(0UL, false, true, 0)]
    [TestCase(21_000UL, true, true, 0)]
    [TestCase(21_000UL, false, true, 1)]
    [TestCase(21_000UL, false, false, 0)]
    public void Inclusion_list_reconstruction_requires_gas_dimensions(ulong gasUsed, bool dimensionsKnown, bool nonemptyList, int executions)
    {
        using TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(gasUsed: gasUsed);
        block.InclusionListTransactions = nonemptyList ? [Build.A.Transaction.TestObject] : [];
        if (dimensionsKnown) block.Header.GasUsedPerDimension = (gasUsed, 0);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Bogota.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(executions));
            Assert.That(env.Recording.LastOptions.HasFlag(ProcessingOptions.ForceSequentialBlockAccessList), Is.False);
        }
    }

    [Test]
    public void Inclusion_list_releases_parent_scope_after_verdict_or_read_failure([Values] bool readFails)
    {
        IWorldStateScopeProvider.IScope parentScope = Substitute.For<IWorldStateScopeProvider.IScope>();
        parentScope.Get(TestItem.AddressA).Returns(_ => readFails
            ? throw new InvalidOperationException("Injected parent read failure") : new Account(0, 100));
        IWorldStateScopeProvider parentProvider = Substitute.For<IWorldStateScopeProvider>();
        parentProvider.TryBeginScope(Arg.Any<BlockHeader>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope?>())
            .Returns(call => { call[2] = parentScope; return true; });
        IWorldStateManager parentReader = Substitute.For<IWorldStateManager>();
        parentReader.CreateResettableWorldState().Returns(parentProvider);
        using TestEnvironment env = new(useFlatDb, releaseSpec: Bogota.Instance, parentReader: parentReader);
        Block block = env.CreateBlock();
        block.InclusionListTransactions = [Build.A.Transaction.WithNonce(0).WithGasPrice(0).WithGasLimit(100_000)
            .WithValue(50).WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject];
        env.Beacon.GetFinalizedHash().Returns(block.Hash);

        try
        {
            if (readFails)
                Assert.Throws<InvalidOperationException>(() => env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.None, NullBlockTracer.Instance));
            else
                Assert.That(env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.None, NullBlockTracer.Instance)[0].IsInclusionListSatisfied, Is.False);

            parentScope.Received(1).Get(TestItem.AddressA);
            parentScope.Received(1).Dispose();
        }
        finally
        {
            block.DisposeAccountChanges();
        }
    }

    [Test]
    public void Inclusion_list_uses_generated_bal_without_reading_unchanged_parent()
    {
        IWorldStateManager parentReader = Substitute.For<IWorldStateManager>();
        using TestEnvironment env = new(useFlatDb, releaseSpec: Bogota.Instance, parentReader: parentReader);
        Block block = env.CreateBlock();
        block.BlockAccessList = null;
        block.EncodedBlockAccessList = null;
        block.GeneratedBlockAccessList = new GeneratedBlockAccessList();
        block.InclusionListTransactions = [Build.A.Transaction.WithNonce(0).WithGasPrice(0).WithGasLimit(100_000)
            .WithValue(50).WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject];

        Block[] processed = env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.None, NullBlockTracer.Instance);

        Assert.That(processed[0].IsInclusionListSatisfied, Is.False);
        parentReader.DidNotReceive().CreateResettableWorldState();
    }

    [Test]
    public void Inclusion_list_uses_each_parent_inside_an_uncommitted_branch()
    {
        using TestEnvironment env = new(useFlatDb, releaseSpec: Bogota.Instance);
        Block first = env.CreateBlock(balance: 25);
        Block second = env.CreateBlock(parent: first, balance: 75);
        Block third = env.CreateBlock(parent: second, balance: 25);
        Transaction omitted = Build.A.Transaction.WithNonce(0).WithGasPrice(0).WithGasLimit(100_000)
            .WithValue(50).WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        first.InclusionListTransactions = [omitted];
        second.InclusionListTransactions = [omitted];
        third.InclusionListTransactions = [Build.A.Transaction.WithNonce(0).WithGasPrice(0).WithGasLimit(100_000)
            .WithValue(90).WithTo(TestItem.AddressB).SignedAndResolved(TestItem.PrivateKeyA).TestObject];
        env.Beacon.GetFinalizedHash().Returns(third.Hash);

        Block[] processed = env.Branch.Process(env.Genesis.Header, [first, second, third], ProcessingOptions.None, NullBlockTracer.Instance);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.Zero, "IL checks must preserve finalized BAL reconstruction");
            Assert.That(processed.Select(block => block.IsInclusionListSatisfied), Is.EqualTo(new[] { false, true, true }));
        }
        foreach (Block block in processed) block.DisposeAccountChanges();
    }

    [Test]
    public void Reconstruction_needs_and_returns_stored_receipts([Values] bool stored)
    {
        using TestEnvironment env = new(useFlatDb);
        TxReceipt[] receipts = [new TxReceipt { StatusCode = 1, GasUsedTotal = 21000, Bloom = Bloom.Empty, Logs = [] }];
        Block block = env.CreateBlock(withTransaction: true,
            receiptsRoot: ReceiptsRootCalculator.Instance.GetReceiptsRoot(receipts, Amsterdam.Instance, null));
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.ReceiptConfig.StoreReceipts = true;
        if (stored) env.Receipts.Insert(block, receipts, ensureCanonical: false);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        long missingReceiptsBefore = Fallbacks(BalCatchUpFallbackReason.NoReceipts);
        (Block _, TxReceipt[] result) = env.Processor.ProcessOne(block, ProcessingOptions.StoreReceipts, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            if (!stored) Assert.That(Fallbacks(BalCatchUpFallbackReason.NoReceipts), Is.GreaterThan(missingReceiptsBefore));
            Assert.That(env.Recording.Calls, Is.EqualTo(stored ? 0 : 1));
            Assert.That(result, Has.Length.EqualTo(stored ? 1 : 0));
            Assert.That(env.State.StateRoot, Is.EqualTo(stored ? block.StateRoot : env.Genesis.StateRoot));
        }
        block.DisposeAccountChanges();
    }

    [Test]
    public void Reconstructed_receipts_reach_subscribers_with_senders_and_contract_addresses()
    {
        using TestEnvironment env = new(useFlatDb);
        Transaction tx = Build.A.Transaction.WithTo(null).WithNonce(3).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LogEntry log = new(TestItem.AddressC, [1, 2], [TestItem.KeccakA]);
        TxReceipt[] downloaded = [new TxReceipt { StatusCode = 1, GasUsedTotal = 60000, Logs = [log] }];
        Block block = env.CreateBlock(transaction: tx,
            receiptsRoot: ReceiptsRootCalculator.Instance.GetReceiptsRoot(downloaded, Amsterdam.Instance, null));
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.ReceiptConfig.StoreReceipts = true;

        // As in forward sync: bodies arrive without senders and receipts are stored before queued processing recovers them.
        tx.SenderAddress = null;
        env.Recovery.TryRecover(block, downloaded, forceRecoverSender: false);
        env.Receipts.Insert(block, downloaded, ensureCanonical: false);
        tx.SenderAddress = TestItem.AddressA;

        TxReceipt[]? published = null;
        env.Branch.BlockProcessed += (_, e) => published = e.TxReceipts;
        env.Branch.Process(env.Genesis.Header, [block], ProcessingOptions.StoreReceipts, NullBlockTracer.Instance);
        TxReceipt stored = env.Receipts.Get(block)[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.Zero);
            Assert.That(published, Has.Length.EqualTo(1));
            TxReceipt receipt = published![0];
            Assert.That(receipt.Sender, Is.EqualTo(TestItem.AddressA));
            Assert.That(receipt.ContractAddress, Is.EqualTo(ContractAddress.From(TestItem.AddressA, 3)));
            Assert.That(receipt.TxHash, Is.EqualTo(tx.Hash));
            Assert.That(receipt.BlockHash, Is.EqualTo(block.Hash));
            Assert.That(receipt.GasUsed, Is.EqualTo(60000UL));
            Assert.That(receipt.Bloom, Is.EqualTo(new Bloom([log])));
            Assert.That(stored.Sender, Is.EqualTo(TestItem.AddressA), "what later readers of the receipt store see");
        }
        block.DisposeAccountChanges();
    }

    [TestCase(null, true)]
    [TestCase(false, true)]
    [TestCase(true, false)]
    public void Disabled_configuration_or_chain_without_access_lists_keeps_normal_execution(bool? enabled, bool accessListChain)
    {
        using TestEnvironment env = new(useFlatDb, enabled, accessListChain);
        Assert.That(env.Processor, Is.Not.InstanceOf<FinalizedBlockAccessListProcessor>());
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
            env.Beacon, tree, new Nethermind.Specs.TestSpecProvider(Amsterdam.Instance), env.ReceiptConfig);
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

        tree.Head.Returns(third);
        Assert.That(policy.CanReconstruct(third.Header), Is.False);
        Block fourth = env.CreateBlock(parent: third);
        tree.FindHeader(fourth.Hash!, BlockTreeLookupOptions.None).Returns(fourth.Header);
        env.Beacon.GetFinalizedHash().Returns(fourth.Hash);
        Assert.That(policy.CanReconstruct(fourth.Header), Is.True);
        Assert.That(policy.CanReconstruct(third.Header), Is.False);
    }

    [Test]
    public void Evaluate_reports_why_a_block_is_not_reconstructed()
    {
        using TestEnvironment env = new(useFlatDb);
        Block first = env.CreateBlock();
        Block second = env.CreateBlock(parent: first);
        Block withTransaction = env.CreateBlock(withTransaction: true);
        env.Beacon.GetFinalizedHash().Returns(first.Hash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Policy.Evaluate(first.Header), Is.EqualTo(BalCatchUpEligibility.Eligible));
            Assert.That(env.Policy.Evaluate(second.Header), Is.EqualTo(BalCatchUpEligibility.NotFinalized));
            Assert.That(env.Policy.Evaluate(withTransaction.Header), Is.EqualTo(BalCatchUpEligibility.Unsupported), "no receipt store");
            Assert.That(env.Policy.Evaluate(env.Genesis.Header), Is.EqualTo(BalCatchUpEligibility.Unsupported));
        }

        env.Beacon.GetFinalizedHash().Returns(Keccak.Zero);
        Assert.That(env.Policy.Evaluate(first.Header), Is.EqualTo(BalCatchUpEligibility.NotFinalized), "no finalized block reported yet");

        IBlockTree tree = Substitute.For<IBlockTree>();
        tree.Head.Returns(first);
        FinalizedBlockAccessListPolicy policy = new(new SyncConfig { ReconstructFinalizedStateFromBlockAccessLists = true },
            env.Beacon, tree, new Nethermind.Specs.TestSpecProvider(Amsterdam.Instance), env.ReceiptConfig);
        Assert.That(policy.Evaluate(first.Header), Is.EqualTo(BalCatchUpEligibility.AlreadyProcessed));
    }

    internal static long Fallbacks(BalCatchUpFallbackReason reason) => MergeMetrics.BalCatchUpFallbacks.GetValueOrDefault(reason);

    internal sealed class TestEnvironment : IDisposable
    {
        private readonly IContainer _container;
        private readonly IReleaseSpec _releaseSpec;
        public IBeaconSyncStrategy Beacon { get; } = Substitute.For<IBeaconSyncStrategy>();
        public ReceiptConfig ReceiptConfig { get; } = new() { StoreReceipts = false, DeferredPersistence = false };
        public IBlockTree Tree { get; }
        public ManualTimestamper Timestamper => (ManualTimestamper)_container.Resolve<ITimestamper>();
        public IWorldState State { get; }
        public IReceiptStorage Receipts { get; }
        public IReceiptsRecovery Recovery => _container.Resolve<IReceiptsRecovery>();
        public IBlockProcessor Processor { get; }
        public IBranchProcessor Branch { get; }
        public RecordingProcessor Recording { get; }
        public FinalizedBlockAccessListPolicy Policy { get; }
        public Block Genesis { get; }

        public TestEnvironment(bool useFlatDb, bool? enabled = true, bool accessListChain = true, IReleaseSpec? releaseSpec = null, ISpecProvider? specProvider = null, IWorldStateManager? parentReader = null)
        {
            _releaseSpec = releaseSpec ?? Amsterdam.Instance;
            specProvider ??= new Nethermind.Specs.TestSpecProvider(_releaseSpec);
            SyncConfig sync = new();
            if (enabled is { } enableCatchUp) sync.ReconstructFinalizedStateFromBlockAccessLists = enableCatchUp;
            Beacon.MergeTransitionFinished.Returns(true);
            _container = new ContainerBuilder().AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = useFlatDb }))
                .AddSingleton(specProvider)
                .AddSingleton<ISyncConfig>(sync)
                .AddSingleton<IReceiptConfig>(ReceiptConfig)
                .AddSingleton<IBeaconSyncStrategy>(Beacon)
                .AddSingleton<FinalizedBlockAccessListPolicy>()
                .AddSingleton<IMainProcessingModule>(new TestProcessingModule(sync, accessListChain ? specProvider : new Nethermind.Specs.TestSpecProvider(Osaka.Instance), parentReader))
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
                State.Commit(_releaseSpec);
                State.RecalculateStateRoot();
                Genesis = Build.A.Block.Genesis.WithStateRoot(State.StateRoot).TestObject;
                State.CommitTree(0);
            }
            Tree.SuggestBlock(Genesis, BlockTreeSuggestOptions.None);
        }

        public Block CreateBlock(bool matchingRoot = true, bool matchingList = true, bool insert = true, bool withTransaction = false, Block? parent = null, UInt256? balance = null, Hash256? receiptsRoot = null, ulong? gasLimit = null, Transaction? transaction = null, ulong gasUsed = 0, ulong? timestamp = null)
        {
            parent ??= Genesis;
            UInt256 finalBalance = balance ?? 25;
            Hash256 root;
            using (State.BeginScope(Genesis.Header))
            {
                State.SubtractFromBalance(TestItem.AddressA, 100 - finalBalance, _releaseSpec, out _);
                State.Commit(_releaseSpec);
                State.RecalculateStateRoot();
                root = State.StateRoot;
            }
            ReadOnlyBlockAccessList list = new([
                new ReadOnlyAccountChanges(TestItem.AddressA, [], [], [new BalanceChange(1, finalBalance)], [], [])], 0);
            byte[] encoded = Rlp.Encode(list).Bytes;
            BlockBuilder builder = Build.A.Block.WithParent(parent).WithDifficulty(0).WithGasUsed(gasUsed).WithStateRoot(matchingRoot ? root : TestItem.KeccakC)
                .WithBlockAccessListHash(matchingList ? Keccak.Compute(encoded) : TestItem.KeccakD);
            if (timestamp is not null) builder.WithTimestamp(timestamp.Value);
            if (withTransaction) builder.WithTransactions(Build.A.Transaction.WithSenderAddress(TestItem.AddressA).TestObject);
            if (transaction is not null) builder.WithTransactions(transaction);
            if (receiptsRoot is not null) builder.WithReceiptsRoot(receiptsRoot);
            if (gasLimit is not null) builder.WithGasLimit(gasLimit.Value);
            Block block = builder.TestObject;
            block.Header.IsPostMerge = true;
            if (insert) Tree.Insert(block.Header);
            // As in forward sync: the downloaded list is stored, and queued processing attaches it only when it
            // matches the header's commitment.
            _container.Resolve<IBlockAccessListStore>().Insert(block.Number, block.Hash!, encoded);
            _container.Resolve<IEnumerable<IBlockPreprocessorStep>>().OfType<BlockAccessListRecoveryStep>().Single()
                .RecoverDataForQueuedProcessing(block);
            return block;
        }

        /// <summary>Sets the clock to just past, or exactly at, the age from which a block counts as caught up.</summary>
        public void SetBlockAge(Block block, bool catchingUp)
        {
            ulong age = FinalizedBlockAccessListProcessor.CatchUpAgeSlots * new BlocksConfig().SecondsPerSlot + (catchingUp ? 1UL : 0UL);
            Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)(block.Timestamp + age)).UtcDateTime);
        }

        public ReadOnlyBlockAccessList StoredList(Block block) =>
            _container.Resolve<IBlockAccessListStore>().Get(block.Number, block.Hash!)!;

        public void Dispose() => _container.Dispose();
    }

    private sealed class TestProcessingModule(ISyncConfig config, ISpecProvider specProvider, IWorldStateManager? parentReader) : Module, IMainProcessingModule
    {
        protected override void Load(ContainerBuilder builder)
        {
            builder.AddScoped<RecordingProcessor>().Bind<IBlockProcessor, RecordingProcessor>()
                .AddModule(new FinalizedBlockAccessListModule(config, specProvider));
            if (parentReader is not null) builder.AddSingleton(parentReader);
        }
    }

    public sealed class RecordingProcessor(IWorldState state) : IBlockProcessor
    {
        public int Calls { get; private set; }
        public ProcessingOptions LastOptions { get; private set; }
        public UInt256 BalanceAtEntry { get; private set; }
        public event Action<Block>? TransactionsExecuted { add { } remove { } }
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

/// <summary>Counts catch-up blocks that execute; the counters are static, so nothing else runs alongside.</summary>
[TestFixture]
[NonParallelizable]
public class FinalizedBlockAccessListMetricTests
{
    [Test]
    public void New_payload_catch_up_blocks_execute_and_are_counted([Values] bool useFlatDb, [Values] bool finalized, [Values] bool catchingUp)
    {
        using FinalizedBlockAccessListTests.TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(finalized ? block.Hash : env.Genesis.Hash);
        env.SetBlockAge(block, catchingUp);
        long engineApiBefore = FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.EngineApi);
        long notFinalizedBefore = FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized);
        long reconstructedBefore = MergeMetrics.BalCatchUpBlocks;
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.EthereumMerge | ProcessingOptions.StoreReceipts, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(1), "the engine API validates by executing");
            Assert.That(MergeMetrics.BalCatchUpBlocks - reconstructedBefore, Is.Zero);
            Assert.That(FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.EngineApi) - engineApiBefore, Is.EqualTo(catchingUp && finalized ? 1 : 0));
            Assert.That(FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized) - notFinalizedBefore, Is.EqualTo(catchingUp && !finalized ? 1 : 0));
        }
    }

    [Test]
    public void Unfinalized_catch_up_blocks_are_counted_only_once_they_are_two_epochs_old([Values] bool useFlatDb, [Values] bool catchingUp)
    {
        using FinalizedBlockAccessListTests.TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock();
        env.Beacon.GetFinalizedHash().Returns(env.Genesis.Hash);
        env.SetBlockAge(block, catchingUp);
        long notFinalizedBefore = FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(1));
            Assert.That(FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized) - notFinalizedBefore, Is.EqualTo(catchingUp ? 1 : 0));
        }
    }

    [Test]
    public void Catch_up_age_follows_slot_duration_changes(
        [Values(1000UL, 1432UL, 2008UL, 2200UL)] ulong timestamp,
        [Values(-1, 0, 1)] int offset,
        [Values] bool engineApi)
    {
        CustomSpecProvider provider = new(((ForkActivation)0, Amsterdam.Instance), (new ForkActivation(0, 2200), Bogota.Instance))
        {
            BeaconChainGenesisTimestamp = 1000,
            Eip8198SlotDurationMs = 6000
        };
        using FinalizedBlockAccessListTests.TestEnvironment env = new(useFlatDb: false, specProvider: provider);
        Block block = env.CreateBlock(timestamp: timestamp);
        env.Beacon.GetFinalizedHash().Returns(env.Genesis.Hash);
        ulong threshold = timestamp switch
        {
            1000 => 1768,
            1432 => 2200,
            2008 => 2488, // Sixteen 12-second slots, then forty-eight 6-second slots.
            _ => 2584
        };
        env.Timestamper.Set(DateTimeOffset.FromUnixTimeSeconds((long)threshold + offset).UtcDateTime);
        long before = FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized);
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, engineApi ? ProcessingOptions.EthereumMerge : ProcessingOptions.None,
            NullBlockTracer.Instance, provider.GetSpec(block.Header));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(1));
            Assert.That(FinalizedBlockAccessListTests.Fallbacks(BalCatchUpFallbackReason.NotFinalized) - before,
                Is.EqualTo(offset > 0 ? 1 : 0));
        }
    }

    [Test]
    public void Blocks_that_do_not_support_reconstruction_are_not_counted([Values] bool useFlatDb)
    {
        using FinalizedBlockAccessListTests.TestEnvironment env = new(useFlatDb);
        Block block = env.CreateBlock(withTransaction: true);
        env.Beacon.GetFinalizedHash().Returns(block.Hash);
        env.SetBlockAge(block, catchingUp: true);
        long[] before = [.. Enum.GetValues<BalCatchUpFallbackReason>().Select(FinalizedBlockAccessListTests.Fallbacks)];
        using IDisposable scope = env.State.BeginScope(env.Genesis.Header);
        env.Processor.ProcessOne(block, ProcessingOptions.None, NullBlockTracer.Instance, Amsterdam.Instance);
        env.Processor.ProcessOne(block, ProcessingOptions.EthereumMerge, NullBlockTracer.Instance, Amsterdam.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(env.Recording.Calls, Is.EqualTo(2));
            Assert.That(Enum.GetValues<BalCatchUpFallbackReason>().Select(FinalizedBlockAccessListTests.Fallbacks), Is.EqualTo(before), "a node without a receipt store never reconstructs blocks with transactions");
        }
    }
}
