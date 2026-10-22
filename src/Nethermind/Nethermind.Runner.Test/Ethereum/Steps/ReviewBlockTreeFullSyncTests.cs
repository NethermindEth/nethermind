// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using CoreBuild = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class ReviewBlockTreeFullSyncTests
{
    [Test]
    public async Task Execute_FullSyncNodeWithMissingChainLevel_HealsCorruptLevels()
    {
        MemDb blockInfosDb = new();
        BlockTreeBuilder builder = CoreBuild.A.BlockTree().WithoutSettingHead.WithBlockInfoDb(blockInfosDb);
        BlockTree seededTree = builder.TestObject;

        Block block0 = CoreBuild.A.Block.WithNumber(0).WithDifficulty(1).TestObject;
        Block block1 = CoreBuild.A.Block.WithNumber(1).WithDifficulty(2).WithParent(block0).TestObject;
        Block block2 = CoreBuild.A.Block.WithNumber(2).WithDifficulty(3).WithParent(block1).TestObject;
        Block block3 = CoreBuild.A.Block.WithNumber(3).WithDifficulty(4).WithParent(block2).TestObject;
        Block block4 = CoreBuild.A.Block.WithNumber(4).WithDifficulty(5).WithParent(block3).TestObject;
        Block block5 = CoreBuild.A.Block.WithNumber(5).WithDifficulty(6).WithParent(block4).TestObject;

        seededTree.SuggestBlock(block0);
        seededTree.SuggestBlock(block1);
        seededTree.SuggestBlock(block2);
        seededTree.SuggestBlock(block3);
        seededTree.SuggestBlock(block4);
        seededTree.SuggestHeader(block5.Header);
        seededTree.TryUpdateMainChain(block0.Header, true, preloadedBlocks: new[] { block0 });
        seededTree.TryUpdateMainChain(block1.Header, true, preloadedBlocks: new[] { block1 });
        seededTree.TryUpdateMainChain(block2.Header, true, preloadedBlocks: new[] { block2 });

        // Drop level 3 so levels 3-5 become phantom (info without recoverable blocks), the corruption
        // a full-sync node used to leave in place under DbBlocksLoader.
        blockInfosDb.Delete(3);
        BlockTree tree = CoreBuild.A.BlockTree().WithoutSettingHead.WithDatabaseFrom(builder).TestObject;

        IInitConfig initConfig = Substitute.For<IInitConfig>();
        initConfig.ProcessingEnabled.Returns(true);
        IBlockProcessingQueue processingQueue = Substitute.For<IBlockProcessingQueue>();
        processingQueue.IsEmpty.Returns(true);

        ReviewBlockTree step = new(
            Substitute.For<IWorldStateManager>(),
            initConfig,
            new SyncConfig { FastSync = false },
            processingQueue,
            tree,
            Substitute.For<IBlockTreeHealer>(),
            LimboLogs.Instance);

        await step.Execute(CancellationToken.None);

        Assert.That(blockInfosDb.Get(3), Is.Null, "full-sync startup must route through the fixer, which deletes the phantom level");
        Assert.That(blockInfosDb.Get(4), Is.Null, "levels above the phantom must be deleted with it");
        Assert.That(blockInfosDb.Get(5), Is.Null, "levels above the phantom must be deleted with it");
        Assert.That(tree.BestKnownNumber, Is.EqualTo(2UL), "best known must fall back to the last intact processed block");
    }
}
