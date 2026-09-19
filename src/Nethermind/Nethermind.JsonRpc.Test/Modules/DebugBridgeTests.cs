// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Evm.State;
using Nethermind.Init;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Find;
using Nethermind.Core.Test.Modules;
using Nethermind.JsonRpc.Modules;
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
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class DebugBridgeTests
{
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
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Block head = Build.A.Block.WithNumber(0).TestObject;
        AddToMainChain(blockTree, head);
        Block target = head;
        ValueHash256 storageRoot = default;
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
            if (i == targetNumber)
            {
                target = head;
                Assert.That(manager.GlobalStateReader.TryGetAccount(target.Header, TestItem.AddressA, out AccountStruct account), Is.True);
                storageRoot = account.StorageRoot;
            }
        }
        manager.FlushCache(CancellationToken.None);
        TrieStore trieStore = (TrieStore)container.Resolve<MainPruningTrieStoreFactory>().PruningTrieStore;
        Assert.That(trieStore.LastPersistedBlockNumber, Is.EqualTo(66));
        INodeStorage nodes = container.Resolve<INodeStorage>();
        Hash256 storageAddress = TestItem.AddressA.ToAccountPath.ToHash256();
        Assert.That(nodes.KeyExists(storageAddress.ValueHash256, TreePath.Empty, storageRoot), Is.True);
        if (retention == TrieRetention.Pruned)
        {
            nodes.Set(storageAddress, TreePath.Empty, storageRoot, null);
            Assert.That(nodes.KeyExists(storageAddress.ValueHash256, TreePath.Empty, storageRoot), Is.False);
        }
        Assert.That(trieStore.HasRoot(target.StateRoot!), Is.True, "the root survives deletion of a storage descendant");
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
        IBlockTree blockTree = container.Resolve<IBlockTree>();
        Block head = Build.A.Block.WithNumber(0).TestObject;
        AddToMainChain(blockTree, head);
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();

        string response = await RpcTest.TestSerializedRequest(debug, method, parameter);

        using (Assert.EnterMultipleScope())
        {
            string expectedTarget = parameter == "0x42" ? "66" : parameter;
            logger.Received(1).Warn($"Cannot rewind the head to {expectedTarget}: block is unknown.");
            Assert.That(response, Is.EqualTo("{\"jsonrpc\":\"2.0\",\"result\":false,\"id\":67}"));
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(head.Hash));
            Assert.That(container.Resolve<IDbProvider>().BlockInfosDb.Get(Keccak.Zero.Bytes), Is.EqualTo(head.Hash!.Bytes.ToArray()));
        }
    }

    private static ResultWrapper<bool> ResetHead(IContainer container, Block target, bool byHash)
    {
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();
        return byHash
            ? debug.debug_resetHead(target.Hash!)
            : debug.debug_setHead(new BlockParameter(target.Number));
    }

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
            persistence.DidNotReceive().DropStateNotReachableFrom(Arg.Any<StateId>());
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
            logManager);

        worldStateManager.When(manager => manager.DropStateNotReachableFrom(Arg.Any<BlockHeader>()))
            .Do(call =>
            {
                Hash256 cleanupHead = call.Arg<BlockHeader>().Hash!;
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(blockTree.Head!.Hash, Is.EqualTo(cleanupHead), "live head must move before cleanup");
                    Assert.That(builder.BlockInfoDb.Get(Keccak.Zero.Bytes), Is.EqualTo(cleanupHead.Bytes.ToArray()), "persisted head must move before cleanup");
                }
            });

        bool updated = bridge.UpdateHeadBlock(hash);

        BlockHeader expectedHead = expected ? blockTree.FindHeader(hash, BlockTreeLookupOptions.None)! : previousHead.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.EqualTo(expected));
            if (target == Target.MissingState)
                Assert.That(logger.LogList, Does.Contain($"Cannot rewind the head to {hash}: state is unavailable for block processing."));
            Assert.That(blockTree.Head!.Hash, Is.EqualTo(expectedHead.Hash));
            // The persisted head pointer (keyed by Keccak.Zero) must follow the live head, never a rejected target.
            Assert.That(builder.BlockInfoDb.Get(Keccak.Zero.Bytes), Is.EqualTo(expectedHead.Hash!.Bytes.ToArray()));
            if (expected)
                worldStateManager.Received(1).DropStateNotReachableFrom(Arg.Is<BlockHeader>(h => h.Hash == expectedHead.Hash));
            else
                worldStateManager.DidNotReceive().DropStateNotReachableFrom(Arg.Any<BlockHeader>());
        }
    }
}
