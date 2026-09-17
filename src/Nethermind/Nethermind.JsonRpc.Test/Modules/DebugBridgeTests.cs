// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Receipts;
using Nethermind.Consensus.Tracing;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.JsonRpc.Modules.DebugModule;
using Nethermind.State;
using Nethermind.Synchronization.ParallelSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class DebugBridgeTests
{
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
        worldStateManager.GlobalStateReader.HasStateForBlock(Arg.Any<BlockHeader>()).Returns(target != Target.MissingState);
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
            worldStateManager);

        bool updated = bridge.UpdateHeadBlock(hash);

        BlockHeader expectedHead = expected ? blockTree.FindHeader(hash, BlockTreeLookupOptions.None)! : previousHead.Header;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(updated, Is.EqualTo(expected));
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
