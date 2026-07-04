// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using CoreBuild = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class ReviewBlockTreeTests
{
    [Test]
    public async Task Execute_WhenPersistedStateBlockExistsLocally_FastForwardsHead()
    {
        RecoverySetup setup = new(persistedNumber: 8, persistedRoot: null);

        await setup.Step.Execute(CancellationToken.None);

        Assert.That(setup.Tree.Head!.Hash, Is.EqualTo(setup.GapBlocks[^1].Hash));
        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(8UL));
    }

    [Test]
    public async Task Execute_WhenPersistedStateRootMismatches_KeepsHead()
    {
        RecoverySetup setup = new(persistedNumber: 8, persistedRoot: TestItem.KeccakB);

        await setup.Step.Execute(CancellationToken.None);

        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(4UL));
    }

    [Test]
    public async Task Execute_WhenPersistedStateBlockIsMissingLocally_KeepsHead()
    {
        RecoverySetup setup = new(persistedNumber: 100, persistedRoot: TestItem.KeccakA);

        await setup.Step.Execute(CancellationToken.None);

        Assert.That(setup.Tree.Head!.Number, Is.EqualTo(4UL));
    }

    private sealed class RecoverySetup
    {
        public BlockTree Tree { get; }
        public ReviewBlockTree Step { get; }
        public Block[] GapBlocks { get; }

        // Tree head is block 4; gap blocks 5..8 are suggested but unprocessed, mirroring the
        // crash-gap shape where the state backend has persisted state ahead of the head. A null
        // persistedRoot means the persisted state matches the junction block's state root.
        public RecoverySetup(ulong persistedNumber, Hash256 persistedRoot)
        {
            Tree = CoreBuild.A.BlockTree().OfChainLength(5).TestObject;

            Block parent = Tree.Head;
            GapBlocks = new Block[4];
            for (int i = 0; i < GapBlocks.Length; i++)
            {
                Block block = CoreBuild.A.Block.WithNumber(parent.Number + 1).WithParent(parent).TestObject;
                Tree.SuggestBlock(block);
                GapBlocks[i] = block;
                parent = block;
            }

            Hash256 junctionRoot = persistedRoot ?? GapBlocks[^1].StateRoot;
            IPersistedStateSource persistedStateSource = Substitute.For<IPersistedStateSource>();
            persistedStateSource.TryGetPersistedState(out Arg.Any<ulong>(), out Arg.Any<Hash256>()).Returns(x =>
            {
                x[0] = persistedNumber;
                x[1] = junctionRoot;
                return true;
            });

            IInitConfig initConfig = Substitute.For<IInitConfig>();
            initConfig.ProcessingEnabled.Returns(true);

            IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
            processingQueue.IsEmpty.Returns(true);

            Step = new ReviewBlockTree(
                Substitute.For<IWorldStateManager>(),
                initConfig,
                new SyncConfig(),
                processingQueue,
                Tree,
                Substitute.For<IBlockTreeHealer>(),
                LimboLogs.Instance,
                persistedStateSource);
        }
    }
}
