// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Test.Modules;
using Nethermind.Init.Steps;
using Nethermind.Specs.ChainSpecStyle;
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
    public async Task Startup_requires_history_only_of_tables_eligible_after_activation(int activationBlock, bool throws)
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
        await Assert.ThatAsync(() => ExecuteStep(tree, activationTimestamp, new SyncConfig(), storedReceipts),
            throws ? Throws.InstanceOf<InvalidOperationException>() : Throws.Nothing);
    }

    [TestCase(true, true, false)]
    [TestCase(false, false, true)]
    [TestCase(true, false, true)]
    public async Task Startup_rejects_fast_sync_that_skips_index_history(bool downloadBodies, bool downloadReceipts, bool throws)
    {
        SyncConfig syncConfig = new()
        {
            FastSync = true,
            SnapSync = true,
            PivotNumber = 1000,
            DownloadBodiesInFastSync = downloadBodies,
            DownloadReceiptsInFastSync = downloadReceipts
        };
        BlockTree tree = CoreBuild.A.BlockTree().OfChainLength(1).TestObject;

        await Assert.ThatAsync(() => ExecuteStep(tree, activationTimestamp: 0, syncConfig, new InMemoryReceiptStorage()),
            throws ? Throws.InstanceOf<InvalidConfigurationException>() : Throws.Nothing);
    }

    [TestCase(true, false, false)]
    [TestCase(false, false, true)]
    [TestCase(true, true, true)]
    public async Task Startup_rejects_receipt_config_that_skips_stored_receipts(bool storeReceipts, bool deriveFromState, bool throws)
    {
        BlockTree tree = CoreBuild.A.BlockTree().OfChainLength(1).TestObject;
        ReceiptConfig receiptConfig = new() { StoreReceipts = storeReceipts, DeriveFromState = deriveFromState };

        await Assert.ThatAsync(() => ExecuteStep(tree, activationTimestamp: 0, new SyncConfig(), new InMemoryReceiptStorage(), receiptConfig),
            throws ? Throws.InstanceOf<InvalidConfigurationException>() : Throws.Nothing);
    }

    private static async Task ExecuteStep(BlockTree tree, ulong activationTimestamp, ISyncConfig syncConfig, IReceiptStorage receiptStorage, IReceiptConfig receiptConfig = null)
    {
        await using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(syncConfig, new InitConfig { ProcessingEnabled = false }, receiptConfig ?? new ReceiptConfig()))
            .AddSingleton(new ChainSpec { Parameters = new ChainParameters { Eip8304TransitionTimestamp = activationTimestamp } })
            .AddSingleton<IBlockTree>(tree)
            .AddSingleton(receiptStorage)
            .AddSingleton<ReviewBlockTree>()
            .Build();

        await container.Resolve<ReviewBlockTree>().Execute(CancellationToken.None);
    }
}
