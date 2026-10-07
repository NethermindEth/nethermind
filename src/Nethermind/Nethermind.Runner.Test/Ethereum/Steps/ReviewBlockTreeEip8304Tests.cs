// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Init.Steps;
using Nethermind.Logging;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.State;
using NSubstitute;
using NUnit.Framework;
using CoreBuild = Nethermind.Core.Test.Builders.Build;

namespace Nethermind.Runner.Test.Ethereum.Steps;

[TestFixture]
public class ReviewBlockTreeEip8304Tests
{
    private const int HeadNumber = 20;

    /// <summary>
    /// At head 20 the earliest unpublished tables start at block 0 (levels 3 and 4) and block 16 (level 2),
    /// and receipts are stored from block 16 only.
    /// </summary>
    [TestCase(16, false, TestName = "Activation at block 16 needs only the stored history")]
    [TestCase(0, true, TestName = "Activation at genesis needs the missing history")]
    public void Startup_requires_history_only_of_tables_eligible_after_activation(int activationBlock, bool throws)
    {
        InMemoryReceiptStorage allReceipts = new();
        BlockTree tree = CoreBuild.A.BlockTree().WithTransactions(allReceipts).OfChainLength(HeadNumber + 1).TestObject;

        InMemoryReceiptStorage storedReceipts = new();
        for (ulong number = 16; number <= HeadNumber; number++)
        {
            Block block = tree.FindBlock(number, BlockTreeLookupOptions.None)!;
            storedReceipts.Insert(block, allReceipts.Get(block));
        }

        ulong activationTimestamp = tree.FindHeader((ulong)activationBlock, BlockTreeLookupOptions.None)!.Timestamp;
        Task execute = CreateStep(tree, activationTimestamp, new SyncConfig(), storedReceipts).Execute(CancellationToken.None);

        if (throws)
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => execute);
        }
        else
        {
            Assert.DoesNotThrowAsync(() => execute);
        }
    }

    [TestCase(true, true, 0UL, false)]
    [TestCase(false, true, 0UL, true)]
    [TestCase(true, false, 0UL, true)]
    [TestCase(true, true, 500UL, false)]
    [TestCase(true, true, 900UL, true)]
    public void Startup_rejects_fast_sync_that_skips_index_history(bool downloadBodies, bool downloadReceipts, ulong ancientReceiptsBarrier, bool throws)
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            PivotNumber = 1000,
            DownloadBodiesInFastSync = downloadBodies,
            DownloadReceiptsInFastSync = downloadReceipts,
            AncientReceiptsBarrier = ancientReceiptsBarrier
        };
        BlockTree tree = CoreBuild.A.BlockTree().OfChainLength(1).TestObject;

        Task execute = CreateStep(tree, activationTimestamp: 0, syncConfig, new InMemoryReceiptStorage()).Execute(CancellationToken.None);

        if (throws)
        {
            Assert.ThrowsAsync<InvalidConfigurationException>(() => execute);
        }
        else
        {
            Assert.DoesNotThrowAsync(() => execute);
        }
    }

    private static ReviewBlockTree CreateStep(BlockTree tree, ulong activationTimestamp, ISyncConfig syncConfig, IReceiptStorage receiptStorage) => new(
        Substitute.For<IWorldStateManager>(),
        Substitute.For<IInitConfig>(),
        syncConfig,
        Substitute.For<IBlockProcessingQueue>(),
        tree,
        Substitute.For<IBlockTreeHealer>(),
        LimboLogs.Instance,
        new ChainSpec { Parameters = new ChainParameters { Eip8304TransitionTimestamp = activationTimestamp } },
        receiptStorage);
}
