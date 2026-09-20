// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Evm.State;
using Nethermind.Init;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Specs;
using Nethermind.Trie.Pruning;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Admin;
using Nethermind.State.Flat;
using Nethermind.State.Flat.History;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State;
using Nethermind.Logging;
using Nethermind.Synchronization;
using Nethermind.Synchronization.FastBlocks;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class DebugBridgeTests
{
    public enum ProcessingState { Running, PausedExecuting, PausedQueued, PausedIdle }

    public enum HistoricalSync { Complete, Headers, Bodies, Receipts, AccessLists, DeleteProgressFloor, BodyFloor, ReceiptFloor, AccessListFloor, BodyAboveHead }

    private static IEnumerable<TestCaseData> HistoricalSyncCases()
    {
        foreach (HistoricalSync history in Enum.GetValues<HistoricalSync>())
            yield return new TestCaseData(false, history);
        yield return new TestCaseData(true, HistoricalSync.Complete);
        yield return new TestCaseData(true, HistoricalSync.Headers);
    }

    [TestCaseSource(nameof(HistoricalSyncCases))]
    public async Task Delete_slice_after_rewind_below_advanced_pivot(bool force, HistoricalSync history)
    {
        TestStateBoundary boundary = new();
        ISyncPeer peer = Substitute.For<ISyncPeer>();
        peer.HeadNumber.Returns(4UL);
        peer.HeadHash.Returns(TestItem.KeccakC);
        peer.TotalDifficulty.Returns(UInt256.MaxValue);
        ISyncPeerPool peerPool = Substitute.For<ISyncPeerPool>();
        peerPool.InitializedPeers.Returns([new Nethermind.Synchronization.Peers.PeerInfo(peer)]);
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new SyncConfig
            {
                FastSync = true,
                PivotNumber = 3,
                PivotHash = TestItem.KeccakA.ToString(),
                StateMinDistanceFromHead = 1,
                HeaderStateDistance = 1,
                AncientBodiesBarrier = history == HistoricalSync.BodyFloor ? 3UL : history == HistoricalSync.BodyAboveHead ? 2UL : 1,
                AncientReceiptsBarrier = history == HistoricalSync.ReceiptFloor ? 3UL : 1,
                AncientBlockAccessListsBarrier = history == HistoricalSync.AccessListFloor ? 3UL : 1
            }, new FlatDbConfig { Enabled = false }))
            .AddSingleton<ISyncPeerPool>(peerPool)
            .AddSingleton<IStateBoundary>(boundary)
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Amsterdam.Instance))
            .Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block[] blocks = new Block[5];
        for (int i = 0; i < blocks.Length; i++)
        {
            blocks[i] = (i == 0 ? Build.A.Block.Genesis : Build.A.Block.WithParent(blocks[i - 1])).WithBlockAccessListHash(TestItem.KeccakA).TestObject;
            AddToMainChain(tree, blocks[i]);
        }
        Assert.That(tree.SyncPivot.BlockNumber, Is.EqualTo(3));
        boundary.BestPersistedState = 4;
        tree.ForkChoiceUpdated(blocks[4].Hash!, blocks[4].Hash!);
        Assert.That(tree.SyncPivot.BlockNumber, Is.EqualTo(4), "finalized persisted progress advances the live pivot");
        tree.LowestInsertedHeader = blocks[history == HistoricalSync.Headers ? 2 : 1].Header;
        ISyncPointers pointers = container.Resolve<ISyncPointers>();
        pointers.LowestInsertedBodyNumber = history is HistoricalSync.Bodies or HistoricalSync.BodyAboveHead ? 2UL : history == HistoricalSync.BodyFloor ? 3UL : 1;
        pointers.LowestInsertedReceiptBlockNumber = history == HistoricalSync.Receipts ? 2UL : history == HistoricalSync.ReceiptFloor ? 3UL : 1;
        pointers.LowestInsertedBlockAccessListBlockNumber = history == HistoricalSync.AccessLists ? 2UL : history == HistoricalSync.AccessListFloor ? 3UL : 1;
        ((ActivatedSyncFeed<BodiesSyncBatch?>)container.Resolve<ISyncFeed<BodiesSyncBatch?>>()).InitializeFeed();
        ((ActivatedSyncFeed<ReceiptsSyncBatch?>)container.Resolve<ISyncFeed<ReceiptsSyncBatch?>>()).InitializeFeed();
        ((ActivatedSyncFeed<BlockAccessListsSyncBatch?>)container.Resolve<ISyncFeed<BlockAccessListsSyncBatch?>>()).InitializeFeed();
        ISyncProgressResolver progress = container.Resolve<ISyncProgressResolver>();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(progress.IsFastBlocksHeadersFinished(), Is.EqualTo(history != HistoricalSync.Headers));
            Assert.That(progress.IsFastBlocksBodiesFinished(), Is.EqualTo(history != HistoricalSync.Bodies));
            Assert.That(progress.IsFastBlocksReceiptsFinished(), Is.EqualTo(history != HistoricalSync.Receipts));
            Assert.That(progress.IsFastBlockAccessListsFinished(), Is.EqualTo(history != HistoricalSync.AccessLists));
        }
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        ulong retainedHead = history == HistoricalSync.BodyAboveHead ? 1UL : 2;
        Assert.That(debug.debug_setHead(new BlockParameter(retainedHead)).Data, Is.True);
        Assert.That(tree.SyncPivot.BlockNumber, Is.EqualTo(4));

        IDbProvider db = container.Resolve<IDbProvider>();
        byte[]? headerProgress = db.MetadataDb.Get(MetadataDbKeys.LowestInsertedFastHeaderHash);
        byte[]? bodyProgress = db.MetadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber);
        byte[]? accessListProgress = db.MetadataDb.Get(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber);
        ResultWrapper<int> result = debug.debug_deleteChainSlice(history == HistoricalSync.DeleteProgressFloor ? 1 : 3, force);
        bool accepted = history == HistoricalSync.Complete;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(accepted ? ResultType.Success : ResultType.Failure));
            if (accepted) Assert.That(result.Data, Is.EqualTo(2));
            else
            {
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
                string expectedError = history switch
                {
                    HistoricalSync.Headers or HistoricalSync.Bodies or HistoricalSync.Receipts or HistoricalSync.AccessLists =>
                        "Historical sync is unfinished or initial synchronization is active; wait for synchronization to complete before deleting chain levels.",
                    HistoricalSync.BodyAboveHead =>
                        "Historical sync progress is above the replacement head; rewind less deeply before deleting chain levels.",
                    _ => "Historical sync progress lies in the deletion range; choose a higher startNumber."
                };
                Assert.That(result.Result.Error, Is.EqualTo(expectedError));
            }
            Assert.That(tree.Head!.Hash, Is.EqualTo(blocks[retainedHead].Hash));
            Assert.That(tree.FindBlock(blocks[3].Hash!, BlockTreeLookupOptions.None), accepted ? Is.Null : Is.Not.Null);
            Assert.That(tree.FindBlock(blocks[4].Hash!, BlockTreeLookupOptions.None), accepted ? Is.Null : Is.Not.Null);
            Assert.That(tree.FindBlock(blocks[1].Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(tree.LowestInsertedHeader!.Hash, Is.EqualTo(blocks[history == HistoricalSync.Headers ? 2 : 1].Hash));
            Assert.That(pointers.LowestInsertedBodyNumber, Is.EqualTo(history is HistoricalSync.Bodies or HistoricalSync.BodyAboveHead ? 2 : history == HistoricalSync.BodyFloor ? 3 : 1));
            Assert.That(pointers.LowestInsertedReceiptBlockNumber, Is.EqualTo(history == HistoricalSync.Receipts ? 2 : history == HistoricalSync.ReceiptFloor ? 3 : 1));
            Assert.That(pointers.LowestInsertedBlockAccessListBlockNumber, Is.EqualTo(history == HistoricalSync.AccessLists ? 2 : history == HistoricalSync.AccessListFloor ? 3 : 1));
            Assert.That(tree.SyncPivot, Is.EqualTo(accepted ? (2UL, blocks[2].Hash!) : (4UL, blocks[4].Hash!)));
            Assert.That(db.MetadataDb.Get(MetadataDbKeys.LowestInsertedFastHeaderHash), Is.EqualTo(headerProgress));
            Assert.That(db.MetadataDb.Get(MetadataDbKeys.LowestInsertedBodyNumber), Is.EqualTo(bodyProgress));
            Assert.That(db.MetadataDb.Get(MetadataDbKeys.LowestInsertedBlockAccessListBlockNumber), Is.EqualTo(accessListProgress));
        }
        if (accepted)
        {
            ISyncModeSelector selector = container.Resolve<ISyncModeSelector>();
            selector.Update();
            Assert.That(selector.Current.HasFlag(SyncMode.Full), Is.True, $"recovery must allow synchronization to resume, got {selector.Current}");
        }
        Block replacement = Build.A.Block.WithParent(blocks[retainedHead]).WithExtraData([0xAB]).TestObject;
        AddToMainChain(tree, replacement);
        Assert.That(tree.Head!.Hash, Is.EqualTo(replacement.Hash));
    }

    [Test]
    public async Task Delete_slice_refuses_initial_sync_modes(
        [Values(SyncMode.FastHeaders, SyncMode.BeaconHeaders, SyncMode.StateNodes, SyncMode.FastSync, SyncMode.UpdatingPivot, SyncMode.DbLoad)] SyncMode mode)
    {
        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        selector.Current.Returns(mode);
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new SyncConfig { FastSync = false }))
            .AddSingleton<ISyncModeSelector>(selector)
            .Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.Genesis.TestObject;
        AddToMainChain(tree, genesis);
        Block pending = Build.A.Block.WithParent(genesis).TestObject;
        tree.SuggestBlock(pending, BlockTreeSuggestOptions.ForceDontSetAsMain);
        tree.SyncPivot = (1, pending.Hash!);
        ResultWrapper<int> result = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create()
            .debug_deleteChainSlice(1, force: true);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
            Assert.That(tree.FindBlock(pending.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(tree.SyncPivot, Is.EqualTo((1UL, pending.Hash!)));
            Assert.That(tree.Head!.Hash, Is.EqualTo(genesis.Hash));
        }
    }

    [Test]
    public async Task Delete_slice_rejects_non_positive_start([Values(-1L, 0L)] long start)
    {
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        ResultWrapper<int> result = debug.debug_deleteChainSlice(start);
        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [Test]
    public async Task Delete_slice_refuses_missing_new_head_body([Values] bool throughRpc)
    {
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block target = Build.A.Block.WithParent(genesis).TestObject;
        Block head = Build.A.Block.WithParent(target).TestObject;
        foreach (Block block in new[] { genesis, target, head }) AddToMainChain(tree, block);
        container.Resolve<IBlockStore>().Delete(target.Number, target.Hash!);
        Assert.That(tree.FindBlock(target.Hash!, BlockTreeLookupOptions.None), Is.Null);
        Assert.That(container.Resolve<IWorldStateManager>().GlobalWorldState.HasRoot(target.Header), Is.True);
        Assert.That(container.Resolve<IWorldStateManager>().GlobalStateReader.HasStateForBlock(target.Header), Is.True);

        if (throughRpc)
        {
            ResultWrapper<int> result = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create()
                .debug_deleteChainSlice(2, force: true);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));
                Assert.That(result.Result.Error, Is.EqualTo("The new head body or state is unavailable for block processing."));
            }
        }
        else
            Assert.That(() => tree.DeleteChainSlice(2, force: true), Throws.InvalidOperationException
                .With.Message.EqualTo("The replacement head block is unavailable."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(tree.Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(tree.FindBlock(head.Hash!, BlockTreeLookupOptions.None), Is.Not.Null);
            Assert.That(tree.FindLevel(2), Is.Not.Null);
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(head.Hash!.Bytes.ToArray()));
        }
    }

    [Test]
    public async Task Refused_mutation_does_not_interrupt_canonical_updates(
        [Values] ChainMutation mutation,
        [Values(ProcessingState.Running, ProcessingState.PausedExecuting, ProcessingState.PausedQueued)] ProcessingState processingState)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule())
            .AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger))).Build();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block head = Build.A.Block.WithParent(genesis).TestObject;
        Block next = Build.A.Block.WithParent(head).TestObject;
        AddToMainChain(tree, genesis);
        AddToMainChain(tree, head);
        tree.SuggestBlock(next);
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        if (processingState != ProcessingState.Running) container.Resolve<IBlockProcessingPauseControl>().Pause();
        tree.IsProcessingBlock = processingState == ProcessingState.PausedExecuting;
        if (processingState == ProcessingState.PausedQueued)
            await container.Resolve<IBlockProcessingQueue>().Enqueue(head, ProcessingOptions.None);
        const string refusalWarning = "Cannot mutate the chain: pause block processing and wait for it to drain.";
        TaskCompletionSource refusing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseRefusal = new();
        logger.When(x => x.Warn(refusalWarning))
            .Do(_ =>
            {
                refusing.SetResult();
                if (!releaseRefusal.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("refusal was not released");
            });
        Task<int> request = Task.Run(() => MutateChain(debug, genesis, mutation));
        bool updated = false;
        Exception? assertionFailure = null;
        try
        {
            try
            {
                await refusing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException($"Did not observe the expected refusal warning: {refusalWarning}", exception);
            }
            updated = tree.TryUpdateMainChain(next.Header, true, true, next);
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
        }
        finally
        {
            releaseRefusal.Set();
            tree.IsProcessingBlock = false;
        }
        await DrainWorkers(request, assertionFailure);
        int result = await request;
        using (Assert.EnterMultipleScope())
        {
            AssertCalls(() => logger.Received(1).Warn(refusalWarning));
            Assert.That(updated, Is.True, "an ineligible request must not acquire maintenance and interrupt a live canonical update");
            Assert.That(result, Is.EqualTo(mutation == ChainMutation.DeleteSlice ? ErrorCodes.ResourceUnavailable : 0));
            Assert.That(tree.Head!.Hash, Is.EqualTo(next.Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(next.Hash!.Bytes.ToArray()));
        }
    }

    [Test]
    public async Task Chain_mutation_requires_paused_and_drained_processing(
        [Values] ChainMutation mutation, [Values] ProcessingState processingState)
    {
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block head = Build.A.Block.WithParent(genesis).TestObject;
        AddToMainChain(tree, genesis);
        AddToMainChain(tree, head);
        IBlockProcessingPauseControl pause = container.Resolve<IBlockProcessingPauseControl>();
        if (processingState != ProcessingState.Running) pause.Pause();
        tree.IsProcessingBlock = processingState == ProcessingState.PausedExecuting;
        if (processingState == ProcessingState.PausedQueued)
            await container.Resolve<IBlockProcessingQueue>().Enqueue(head, ProcessingOptions.None);
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();

        int result = MutateChain(debug, genesis, mutation);

        bool accepted = processingState == ProcessingState.PausedIdle;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result, Is.EqualTo(accepted ? 1 : mutation == ChainMutation.DeleteSlice ? ErrorCodes.ResourceUnavailable : 0));
            Assert.That(tree.Head!.Hash, Is.EqualTo(accepted ? genesis.Hash : head.Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes),
                Is.EqualTo((accepted ? genesis : head).Hash!.Bytes.ToArray()));
        }
        if (!accepted && processingState != ProcessingState.PausedQueued)
        {
            tree.IsProcessingBlock = false;
            pause.Pause();
            Assert.That(MutateChain(debug, genesis, mutation), Is.EqualTo(1), "retry must succeed after processing becomes paused and idle");
        }
    }

    [TestCase("latest", 2UL)]
    [TestCase("safe", 1UL)]
    [TestCase("finalized", 0UL)]
    [TestCase("hash", 1UL)]
    public async Task SetHead_resolves_tags_and_hashes(string parameter, ulong expectedNumber)
    {
        await using IContainer container = new ContainerBuilder().AddModule(new TestNethermindModule()).Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree tree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block safe = Build.A.Block.WithParent(genesis).TestObject;
        Block head = Build.A.Block.WithParent(safe).TestObject;
        foreach (Block block in new[] { genesis, safe, head }) AddToMainChain(tree, block);
        tree.ForkChoiceUpdated(genesis.Hash, safe.Hash);
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();

        string response = await RpcTest.TestSerializedRequest(debug, "debug_setHead", parameter == "hash" ? safe.Hash!.ToString() : parameter);

        Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":true,\"id\":67}"));
        Assert.That(tree.Head!.Number, Is.EqualTo(expectedNumber));
    }

    public enum TrieRetention { Pruned, AtBoundary, Archive }

    [Test]
    public async Task Head_reset_respects_trie_retention([Values] TrieRetention retention, [Values] bool byHash)
    {
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(
                new FlatDbConfig { Enabled = false },
                new InitConfig { StateDbKeyScheme = INodeStorage.KeyScheme.HalfPath },
                new SyncConfig { TrieHealing = false, SnapServingEnabled = false },
                new PruningConfig { Mode = retention == TrieRetention.Archive ? PruningMode.None : PruningMode.Memory, PruningBoundary = 64 }))
            .Build();
        IWorldState worldState = container.Resolve<IMainProcessingContext>().WorldState;
        IWorldStateManager manager = container.Resolve<IWorldStateManager>();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Block head = Build.A.Block.WithNumber(0).TestObject;
        AddToMainChain(blockTree, head);
        Block target = head;
        int targetNumber = retention == TrieRetention.AtBoundary ? 2 : 1;
        for (int i = 1; i <= 66; i++)
        {
            using (worldState.BeginScope(head.Header))
            {
                if (i == 1) worldState.CreateAccount(TestItem.AddressA, 1);
                worldState.Set(new StorageCell(TestItem.AddressA, 0), (UInt256)(i + 6));
                worldState.Commit(Frontier.Instance);
                worldState.CommitTree((ulong)i);
                head = Build.A.Block.WithParent(head).WithStateRoot(worldState.StateRoot).TestObject;
            }
            AddToMainChain(blockTree, head);
            if (i == targetNumber) target = head;
        }
        manager.FlushCache(CancellationToken.None);
        TrieStore trieStore = (TrieStore)container.Resolve<MainPruningTrieStoreFactory>().PruningTrieStore;
        Assert.That(trieStore.LastPersistedBlockNumber, Is.EqualTo(66));
        Assert.That(trieStore.HasRoot(target.StateRoot!), Is.True, "root presence alone does not enforce retention");
        ResultWrapper<bool> result = ResetHead(container, target, byHash);

        bool accepted = retention != TrieRetention.Pruned;
        Hash256 expectedHead = (accepted ? target : head).Hash!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(result.Data, Is.EqualTo(accepted));
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(expectedHead));
            Assert.That(blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(expectedHead));
            Assert.That(blockTree.IsMainChain(head.Header), Is.EqualTo(!accepted));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(expectedHead.Bytes.ToArray()));
        }
        manager.GlobalStateReader.GetStorage(blockTree.Head!.Header, TestItem.AddressA, 0, out UInt256 stored);
        Assert.That(stored, Is.EqualTo((UInt256)(accepted ? targetNumber + 6 : 72)));
    }

    [Test]
    public async Task Head_reset_accepts_live_snapshot_with_history_enabled([Values] bool byHash)
    {
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true, HistoryEnabled = true, Layout = FlatLayout.Flat }))
            .Build();
        IWorldState state = container.Resolve<IMainProcessingContext>().WorldState;
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        using (state.BeginScope(IWorldState.PreGenesis))
        {
            state.Commit(Frontier.Instance);
            state.CommitTree(0);
        }
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        AddToMainChain(blockTree, genesis);
        Block target;
        using (state.BeginScope(genesis.Header))
        {
            state.CreateAccount(TestItem.AddressA, 1);
            state.Commit(Frontier.Instance);
            state.CommitTree(1);
            target = Build.A.Block.WithParent(genesis).WithStateRoot(state.StateRoot).TestObject;
        }
        AddToMainChain(blockTree, target);
        Block head;
        using (state.BeginScope(target.Header))
        {
            state.AddToBalance(TestItem.AddressA, 1, Frontier.Instance);
            state.Commit(Frontier.Instance);
            state.CommitTree(2);
            head = Build.A.Block.WithParent(target).WithStateRoot(state.StateRoot).TestObject;
        }
        AddToMainChain(blockTree, head);
        ResultWrapper<bool> result = ResetHead(container, target, byHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.True);
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(target.Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(target.Hash!.Bytes.ToArray()));
            Assert.That(container.Resolve<IWorldStateManager>().GlobalWorldState.HasRoot(head.Header), Is.False, "abandoned snapshot is removed");
        }
        Block replacement;
        using (state.BeginScope(target.Header))
        {
            Assert.That(state.GetBalance(TestItem.AddressA), Is.EqualTo((UInt256)1));
            state.AddToBalance(TestItem.AddressA, 2, Frontier.Instance);
            state.Commit(Frontier.Instance);
            state.CommitTree(2);
            replacement = Build.A.Block.WithParent(target).WithStateRoot(state.StateRoot).TestObject;
        }
        IStateReader reader = container.Resolve<IWorldStateManager>().GlobalStateReader;
        Assert.That(reader.TryGetAccount(replacement.Header, TestItem.AddressA, out AccountStruct account), Is.True);
        Assert.That(account.Balance, Is.EqualTo((UInt256)3));
    }

    [TestCase("debug_setHead", "0x42")]
    [TestCase("debug_setHead", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [TestCase("debug_resetHead", "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Head_reset_unknown_target_returns_false_without_changing_head(string method, string parameter)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule())
            .AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger)))
            .Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Block head = Build.A.Block.WithNumber(0).TestObject;
        AddToMainChain(blockTree, head);
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();

        string response = await RpcTest.TestSerializedRequest(debug, method, parameter);

        using (Assert.EnterMultipleScope())
        {
            string expectedTarget = parameter == "0x42" ? "66" : parameter;
            AssertCalls(() => logger.Received(1).Warn($"Cannot rewind the head to {expectedTarget}: block is unknown."));
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(head.Hash!.Bytes.ToArray()));
        }
    }

    public enum ChainMutation { ResetByHash, ResetByNumber, DeleteSlice }

    [Test]
    public async Task Chain_mutation_refuses_overlap_across_modules(
        [Values] ChainMutation firstMutation, [Values] ChainMutation secondMutation, [Values] bool mutationThrows)
    {
        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);
        ObservedPersistenceManager? persistence = null;
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig { Enabled = true }))
            .AddSingleton<ILogManager>(new OneLoggerLogManager(new ILogger(logger)))
            .AddDecorator<IPersistenceManager>((_, inner) => persistence = new ObservedPersistenceManager(inner))
            .Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        IWorldState state = container.Resolve<IMainProcessingContext>().WorldState;
        Block[] blocks = new Block[3];
        for (int i = 0; i < blocks.Length; i++)
        {
            BlockHeader? parent = i == 0 ? IWorldState.PreGenesis : blocks[i - 1].Header;
            using (state.BeginScope(parent))
            {
                if (i == 0) state.CreateAccount(TestItem.AddressA, 1);
                else state.AddToBalance(TestItem.AddressA, 1, Frontier.Instance);
                state.Commit(Frontier.Instance);
                state.CommitTree((ulong)i);
                blocks[i] = (i == 0 ? Build.A.Block.WithNumber(0) : Build.A.Block.WithParent(blocks[i - 1]))
                    .WithStateRoot(state.StateRoot).TestObject;
            }
            AddToMainChain(blockTree, blocks[i]);
        }
        IRpcModuleFactory<IDebugRpcModule> factory = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>();
        IDebugRpcModule first = factory.Create();
        await using ILifetimeScope wrappedScope = container.BeginLifetimeScope(builder => builder
            .AddSingleton<IBlockTree>(new BlockTreeOverlay(blockTree.AsReadOnly(), blockTree))
            .AddSingleton<IGethStyleTracer>(Substitute.For<IGethStyleTracer>()));
        IDebugRpcModule second = wrappedScope.Resolve<IDebugRpcModule>();
        // Explicit rendezvous holds the mutation open; timeouts only prevent a hung test.
        TaskCompletionSource mutationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim releaseMutation = new();
        InvalidOperationException injectedFailure = new("mutation failure");
        int cleanupCalls = 0;
        void BlockFirstMutation()
        {
            mutationEntered.SetResult();
            if (!releaseMutation.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("mutation was not released");
            if (mutationThrows) throw injectedFailure;
        }
        persistence!.BeforeDrop = () =>
        {
            if (Interlocked.Increment(ref cleanupCalls) == 1 && firstMutation != ChainMutation.DeleteSlice)
                BlockFirstMutation();
        };
        if (firstMutation == ChainMutation.DeleteSlice)
            blockTree.NewHeadBlock += (_, args) =>
            {
                if (args.Block.Hash == blocks[1].Hash) BlockFirstMutation();
            };
        Task<(int? Result, Exception? Failure)> firstTask = Task.Run<(int?, Exception?)>(() =>
        {
            try
            {
                return (MutateChain(first, blocks[1], firstMutation), null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        });
        int? overlappingResult = null;
        int callsWhileBlocked = 0;
        Hash256? headWhileBlocked = null;
        byte[]? persistedHeadWhileBlocked = null;
        bool targetRetained = false;
        IAdminRpcModule admin = container.Resolve<IRpcModuleFactory<IAdminRpcModule>>().Create();
        TaskCompletionSource<(bool Result, Exception? Failure)> resumed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread resumeThread = new(() =>
        {
            try { resumed.SetResult((admin.admin_resumeBlockProcessing().Data, null)); }
            catch (Exception exception) { resumed.SetResult((false, exception)); }
        })
        { IsBackground = true };
        Exception? assertionFailure = null;
        try
        {
            await mutationEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            overlappingResult = MutateChain(second, blocks[0], secondMutation);
            Assert.That(blockTree.TryUpdateMainChain(blocks[2].Header, true, true, blocks[2]), Is.False,
                "forkchoice and downloader writes must not move the head during maintenance");
            Assert.That(() => blockTree.DeleteChainSlice(1, force: true), Throws.InvalidOperationException,
                "direct tree deletion must report maintenance refusal without deleting levels");
            string refusal = secondMutation == ChainMutation.DeleteSlice
                ? "Cannot delete the chain slice from 1: chain mutation contention or overlapping maintenance; retry the request."
                : $"Cannot rewind the head to {(secondMutation == ChainMutation.ResetByHash ? blocks[0].Hash!.ToString() : "0")}: chain mutation contention or overlapping maintenance; retry the request.";
            AssertCalls(() => logger.Received().Warn(refusal));
            await using (IContainer independent = new ContainerBuilder().AddModule(new TestNethermindModule()).Build())
            {
                independent.Resolve<IBlockProcessingPauseControl>().Pause();
                IBlockTree independentTree = independent.Resolve<IBlockTree>();
                Block genesis = Build.A.Block.WithNumber(0).TestObject;
                AddToMainChain(independentTree, genesis);
                Assert.That(ResetHead(independent, genesis, byHash: true).Data, Is.True,
                    "maintenance on another node must remain independent");
            }
            callsWhileBlocked = Volatile.Read(ref cleanupCalls);
            headWhileBlocked = blockTree.Head?.Hash;
            persistedHeadWhileBlocked = container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes);
            targetRetained = blockTree.FindBlock(blocks[1].Hash!, BlockTreeLookupOptions.None) is not null;
            resumeThread.Start();
            Assert.That(SpinWait.SpinUntil(() => resumed.Task.IsCompleted ||
                (resumeThread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(10)), Is.True);
            Assert.That(resumed.Task.IsCompleted, Is.False, "admin resume must wait for state cleanup");
            Assert.That(container.Resolve<IBlockProcessingPauseControl>().IsPaused, Is.True);
        }
        catch (Exception exception)
        {
            assertionFailure = exception;
        }
        finally
        {
            releaseMutation.Set();
        }
        Task workers = (resumeThread.ThreadState & ThreadState.Unstarted) == 0
            ? Task.WhenAll(firstTask, resumed.Task)
            : firstTask;
        await DrainWorkers(workers, assertionFailure);
        (int? Result, Exception? Failure) firstOutcome = await firstTask;
        (bool resumeResult, Exception? resumeFailure) = await resumed.Task;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resumeFailure, Is.Null);
            Assert.That(resumeResult, Is.True);
        }
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        int retryResult = MutateChain(second, blocks[0], secondMutation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(overlappingResult, Is.EqualTo(secondMutation == ChainMutation.DeleteSlice ? ErrorCodes.ResourceUnavailable : 0));
            Assert.That(callsWhileBlocked, Is.EqualTo(firstMutation == ChainMutation.DeleteSlice ? 0 : 1));
            Assert.That(headWhileBlocked, Is.EqualTo(blocks[1].Hash));
            Assert.That(persistedHeadWhileBlocked, Is.EqualTo(blocks[1].Hash!.Bytes.ToArray()));
            Assert.That(targetRetained, Is.True, "an overlapping deletion must not remove the reset target");
            Assert.That(firstOutcome.Failure, mutationThrows ? Is.SameAs(injectedFailure) : Is.Null);
            Assert.That(firstOutcome.Result, Is.EqualTo(mutationThrows ? (int?)null : 1));
            int expectedRetry = secondMutation == ChainMutation.DeleteSlice && firstMutation != ChainMutation.DeleteSlice ? 2 : 1;
            Assert.That(retryResult, Is.EqualTo(expectedRetry), "the gate must be released after success or failure");
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(blocks[0].Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(blocks[0].Hash!.Bytes.ToArray()));
        }
    }

    private static async Task DrainWorkers(Task workers, Exception? assertionFailure)
    {
        try
        {
            await workers.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception cleanupFailure) when (assertionFailure is not null)
        {
            throw new AggregateException(assertionFailure, cleanupFailure);
        }
        if (assertionFailure is not null) ExceptionDispatchInfo.Capture(assertionFailure).Throw();
    }

    private sealed class ObservedPersistenceManager(IPersistenceManager inner) : IPersistenceManager
    {
        public Action? BeforeDrop { get; set; }
        public State.Flat.Persistence.IPersistence.IPersistenceReader LeaseReader() => inner.LeaseReader();
        public StateId GetCurrentPersistedStateId() => inner.GetCurrentPersistedStateId();
        public Task AddToPersistence(StateId latestSnapshot) => inner.AddToPersistence(latestSnapshot);
        public StateId FlushToPersistence(CancellationToken cancellationToken) => inner.FlushToPersistence(cancellationToken);
        public void ResetPersistedStateId() => inner.ResetPersistedStateId();
        public void DropStateNotReachableFrom(in StateId head)
        {
            BeforeDrop?.Invoke();
            inner.DropStateNotReachableFrom(head);
        }
    }

    private static int MutateChain(IDebugRpcModule debug, Block target, ChainMutation mutation) => mutation switch
    {
        ChainMutation.ResetByHash => debug.debug_resetHead(target.Hash!).Data ? 1 : 0,
        ChainMutation.ResetByNumber => debug.debug_setHead(new BlockParameter(target.Number)).Data ? 1 : 0,
        _ => DeleteSlice(debug, target),
    };

    private static int DeleteSlice(IDebugRpcModule debug, Block target)
    {
        ResultWrapper<int> result = debug.debug_deleteChainSlice((long)target.Number + 1, force: true);
        return result.Result.ResultType == ResultType.Success ? result.Data : result.ErrorCode;
    }

    private static void AssertCalls(Action assertion) => Assert.That(assertion, Throws.Nothing);

    private static ResultWrapper<bool> ResetHead(IContainer container, Block target, bool byHash)
    {
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        return ResetHead(debug, target, byHash);
    }

    private static ResultWrapper<bool> ResetHead(IDebugRpcModule debug, Block target, bool byHash) => byHash
        ? debug.debug_resetHead(target.Hash!)
        : debug.debug_setHead(new BlockParameter(target.Number));

    private static void AddToMainChain(IBlockTree blockTree, Block block)
    {
        blockTree.SuggestBlock(block);
        blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
    }

    public enum UnavailableState { Missing, Historical, RestrictedHistorical }

    [Test]
    public async Task Head_reset_refuses_state_unavailable_for_processing([Values] UnavailableState state, [Values] bool byHash)
    {
        IPersistenceManager persistence = Substitute.For<IPersistenceManager>();
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new FlatDbConfig
            {
                Enabled = true,
                HistoryEnabled = true,
                HistoryRetention = HistoryRetentionMode.Rolling,
                HistoryRetentionBlocks = 1,
                Layout = FlatLayout.Flat,
                HistorySliceAddresses = TestItem.AddressA.ToString()
            }))
            .AddSingleton(persistence)
            .Build();
        container.Resolve<IBlockProcessingPauseControl>().Pause();
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Block genesis = Build.A.Block.WithNumber(0).TestObject;
        Block target = Build.A.Block.WithParent(genesis).WithStateRoot(TestItem.KeccakB).TestObject;
        Block head = Build.A.Block.WithParent(target).WithStateRoot(TestItem.KeccakA).TestObject;
        foreach (Block block in new[] { genesis, target, head })
        {
            AddToMainChain(blockTree, block);
        }
        persistence.GetCurrentPersistedStateId().Returns(new StateId(head.Header));
        HistoryAvailability availability = container.Resolve<HistoryAvailability>();
        byte format = container.Resolve<HistoryRowFormat>().FormatVersion;
        IColumnsDb<FlatHistoryColumns> columns = container.Resolve<IColumnsDb<FlatHistoryColumns>>();
        if (state != UnavailableState.Missing)
        {
            using IColumnsWriteBatch<FlatHistoryColumns> batch = columns.StartWriteBatch();
            HistoryAvailability.MarkBlock(batch.GetColumnBatch(FlatHistoryColumns.AvailableBlocks), target.Number, target.StateRoot!, format);
            availability.PublishWatermark(head.Number, format);
        }
        if (state == UnavailableState.RestrictedHistorical)
        {
            availability.PublishScope(HistoryKeyLayout.ToFlatStateKey(TestItem.AddressA.ToAccountPath.Bytes), 0);
            availability.PublishGlobalFloor(head.Number);
        }
        IWorldStateManager worldState = container.Resolve<IWorldStateManager>();
        Assert.That(worldState.GlobalStateReader.HasStateForBlock(target.Header), Is.EqualTo(state != UnavailableState.Missing), "only retained historical state is readable");
        Assert.That(worldState.GlobalWorldState.HasRoot(head.Header), Is.True, "persisted state remains writable");
        ResultWrapper<bool> result = ResetHead(container, target, byHash);
        ResultWrapper<int> deletion = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create()
            .debug_deleteChainSlice((long)head.Number, force: true);
        Assert.That(deletion.ErrorCode, Is.EqualTo(ErrorCodes.ResourceUnavailable));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data, Is.False);
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(blockTree.BestSuggestedHeader!.Hash, Is.EqualTo(head.Hash));
            Assert.That(blockTree.IsMainChain(head.Header), Is.True);
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(head.Hash!.Bytes.ToArray()));
            Assert.That(worldState.GlobalWorldState.HasRoot(target.Header), Is.False);
            IFlatDbManager flatDbManager = container.Resolve<IFlatDbManager>();
            Assert.That(flatDbManager.HasStateForBlock(new StateId(target.Header), ResourcePool.Usage.PostMainBlockProcessing), Is.False);
            Assert.That(flatDbManager.HasStateForBlock(new StateId(target.Header), ResourcePool.Usage.ReadOnlyProcessingEnv), Is.EqualTo(state != UnavailableState.Missing));
            AssertCalls(() => persistence.DidNotReceive().DropStateNotReachableFrom(Arg.Any<StateId>()));
        }
    }

    public enum Target { Unknown, HeaderOnly, CurrentHead, Ancestor, MissingState, AboveHead, SideBranch }

    [TestCase(Target.Unknown, false)]
    [TestCase(Target.HeaderOnly, false)]
    [TestCase(Target.CurrentHead, true)]
    [TestCase(Target.Ancestor, true)]
    [TestCase(Target.MissingState, false)]
    [TestCase(Target.AboveHead, false)]
    [TestCase(Target.SideBranch, false)]
    public void UpdateHeadBlock_MovesTheLiveHeadBeforeDroppingState(Target target, bool expected)
    {
        BlockTreeBuilder builder = Build.A.BlockTree().OfChainLength(3);
        BlockTree blockTree = builder.TestObject;
        Block previousHead = blockTree.Head!;
        BlockHeader headerOnly = Build.A.BlockHeader.WithParent(previousHead.Header).TestObject;
        blockTree.SuggestHeader(headerOnly);
        BlockHeader ancestor = blockTree.FindHeader(1, BlockTreeLookupOptions.None)!;
        Block future = Build.A.Block.WithParent(previousHead).TestObject;
        Block sibling = Build.A.Block.WithParent(blockTree.FindBlock(0, BlockTreeLookupOptions.None)!).WithExtraData([0xAB]).TestObject;
        blockTree.SuggestBlock(future);
        blockTree.SuggestBlock(sibling);
        Hash256 hash = target switch
        {
            Target.Unknown => TestItem.KeccakA,
            Target.HeaderOnly => headerOnly.Hash!,
            Target.CurrentHead => previousHead.Hash!,
            Target.AboveHead => future.Hash!,
            Target.SideBranch => sibling.Hash!,
            _ => ancestor.Hash!,
        };
        IWorldStateManager worldStateManager = Substitute.For<IWorldStateManager>();
        worldStateManager.GlobalWorldState.HasRoot(Arg.Any<BlockHeader>()).Returns(target != Target.MissingState);
        worldStateManager.GlobalStateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(true);
        IBlockProcessingPauseControl pauseControl = Substitute.For<IBlockProcessingPauseControl>();
        pauseControl.IsPaused.Returns(true);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.IsEmpty.Returns(true);
        TestLogger logger = new();
        ILogManager logManager = new OneLoggerLogManager(new ILogger(logger));
        DebugBridge bridge = new(
            Substitute.For<IConfigProvider>(),
            Substitute.For<IReadOnlyDbProvider>(),
            Substitute.For<IGethStyleTracer>(),
            blockTree,
            Substitute.For<IReceiptStorage>(),
            Substitute.For<IReceiptFinder>(),
            Substitute.For<IReceiptsMigration>(),
            Substitute.For<ISpecProvider>(),
            Substitute.For<ISyncModeSelector>(),
            Substitute.For<IBadBlockStore>(),
            Substitute.For<IBlockStore>(),
            worldStateManager,
            logManager, new BlockTreeMutationLock(), pauseControl, processingQueue,
            Substitute.For<ISyncProgressResolver>(), Substitute.For<ISyncPointers>());

        List<(Hash256 CleanupTarget, Hash256? LiveHead, byte[]? PersistedHead)> cleanupHeads = [];
        worldStateManager.When(manager => manager.DropStateNotReachableFrom(Arg.Any<BlockHeader>()))
            .Do(call => cleanupHeads.Add((call.Arg<BlockHeader>().Hash!, blockTree.Head?.Hash, builder.BlockInfoDb.Get(Keccak.Zero.Bytes))));

        bool updated = bridge.UpdateHeadBlock(hash);

        BlockHeader expectedHead = expected ? blockTree.FindHeader(hash, BlockTreeLookupOptions.None)! : previousHead.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.EqualTo(expected));
            foreach ((Hash256 cleanupTarget, Hash256? liveHead, byte[]? persistedHead) in cleanupHeads)
            {
                Assert.That(liveHead, Is.EqualTo(cleanupTarget), "live head must move before cleanup");
                Assert.That(persistedHead, Is.EqualTo(cleanupTarget.Bytes.ToArray()), "persisted head must move before cleanup");
            }
            if (target == Target.MissingState)
                Assert.That(logger.LogList, Does.Contain($"Cannot rewind the head to {hash}: state is unavailable for block processing."));
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(expectedHead.Hash));
            // The persisted head pointer (keyed by Keccak.Zero) must follow the live head, never a rejected target.
            Assert.That(builder.BlockInfoDb.Get(Keccak.Zero.Bytes), Is.EqualTo(expectedHead.Hash!.Bytes.ToArray()));
            if (expected)
                AssertCalls(() => worldStateManager.Received(1).DropStateNotReachableFrom(Arg.Is<BlockHeader>(h => h.Hash == expectedHead.Hash)));
            else
                AssertCalls(() => worldStateManager.DidNotReceive().DropStateNotReachableFrom(Arg.Any<BlockHeader>()));
        }
    }
}
