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

        ChainSpec chainSpec = new()
        {
            Parameters = new ChainParameters { Eip8304TransitionTimestamp = tree.FindHeader((ulong)activationBlock, BlockTreeLookupOptions.None)!.Timestamp }
        };
        ReviewBlockTree step = new(
            Substitute.For<IWorldStateManager>(),
            Substitute.For<IInitConfig>(),
            new SyncConfig(),
            Substitute.For<IBlockProcessingQueue>(),
            tree,
            Substitute.For<IBlockTreeHealer>(),
            LimboLogs.Instance,
            chainSpec,
            storedReceipts);

        Task execute = step.Execute(CancellationToken.None);

        if (throws)
        {
            Assert.ThrowsAsync<InvalidOperationException>(() => execute);
        }
        else
        {
            Assert.DoesNotThrowAsync(() => execute);
        }
    }
}
