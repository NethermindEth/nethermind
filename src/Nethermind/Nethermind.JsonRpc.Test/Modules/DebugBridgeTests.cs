// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
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
            blockTree.SuggestBlock(block);
            blockTree.TryUpdateMainChain(block.Header, wereProcessed: true, forceUpdateHeadBlock: true, block);
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
        IDebugRpcModule debug = container.Resolve<IRpcModuleFactory<IDebugRpcModule>>().Create();

        ResultWrapper<bool> result = byHash
            ? debug.debug_resetHead(target.Hash!)
            : debug.debug_setHead(new BlockParameter(target.Number));

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
