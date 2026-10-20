// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Receipts;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Init.Steps.Migrations;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Runner.Test.Ethereum.Steps.Migrations;

public class ReceiptFixMigrationTests
{
    [Test]
    public async Task Rejects_downloaded_receipts_that_do_not_match_the_header_root()
    {
        InMemoryReceiptStorage canonicalReceipts = new();
        IBlockTree blockTree = Core.Test.Builders.Build.A.BlockTree().WithTransactions(canonicalReceipts).OfChainLength(6).TestObject;
        Block block = blockTree.FindBlock(1, BlockTreeLookupOptions.None)!;
        TxReceipt[] validReceipts = canonicalReceipts.Get(block);
        TxReceipt[] forgedReceipts = [.. validReceipts];
        forgedReceipts[0] = Core.Test.Builders.Build.A.Receipt.WithTransactionHash(validReceipts[0].TxHash!).WithGasUsedTotal(validReceipts[0].GasUsedTotal + 1).WithLogs().TestObject;
        InMemoryReceiptStorage nodeReceipts = new();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<ReadOnlyMemory<int>>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromResult<IOwnedReadOnlyList<TxReceipt[]?>>(new ArrayPoolList<TxReceipt[]?>(1) { forgedReceipts }),
                _ => Task.FromResult<IOwnedReadOnlyList<TxReceipt[]?>>(new ArrayPoolList<TxReceipt[]?>(1) { validReceipts }));
        PeerInfo peerInfo = new(syncPeer);
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SyncPeerAllocation allocation = new(AllocationContexts.Receipts);
                allocation.AllocatePeer(peerInfo);
                return Task.FromResult(allocation);
            });

        ReceiptFixMigration migration = new(
            blockTree,
            nodeReceipts,
            syncPeerPool,
            new SyncConfig { FixReceipts = true, FixReceiptsStartingBlock = 1, FixReceiptsLastBlock = 1 },
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        await migration.Run(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            syncPeerPool.Received(1).ReportBreachOfProtocol(peerInfo, DisconnectReason.InvalidReceiptRoot, Arg.Any<string>());
            Assert.That(nodeReceipts.Get(block), Has.Length.EqualTo(validReceipts.Length));
            Assert.That(nodeReceipts.Get(block)[0].GasUsedTotal, Is.EqualTo(validReceipts[0].GasUsedTotal));
        }
    }

    [Test]
    public async Task Accepts_downloaded_receipts_with_zero_blooms_like_the_receipts_sync_feed()
    {
        InMemoryReceiptStorage canonicalReceipts = new();
        IBlockTree blockTree = Core.Test.Builders.Build.A.BlockTree()
            .WithTransactions(canonicalReceipts, static (_, _) => [Core.Test.Builders.Build.A.LogEntry.TestObject])
            .OfChainLength(6).TestObject;
        Block block = blockTree.FindBlock(1, BlockTreeLookupOptions.None)!;
        TxReceipt[] validReceipts = canonicalReceipts.Get(block);
        TxReceipt[] zeroBloomReceipts = Array.ConvertAll(validReceipts, static r => new TxReceipt(r) { Bloom = Bloom.Empty });
        InMemoryReceiptStorage nodeReceipts = new();

        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.GetReceipts(Arg.Any<IReadOnlyList<Hash256>>(), Arg.Any<ReadOnlyMemory<int>>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IOwnedReadOnlyList<TxReceipt[]?>>(new ArrayPoolList<TxReceipt[]?>(1) { zeroBloomReceipts }));
        PeerInfo peerInfo = new(syncPeer);
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        syncPeerPool.Allocate(Arg.Any<IPeerAllocationStrategy>(), Arg.Any<AllocationContexts>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                SyncPeerAllocation allocation = new(AllocationContexts.Receipts);
                allocation.AllocatePeer(peerInfo);
                return Task.FromResult(allocation);
            });

        ReceiptFixMigration migration = new(
            blockTree,
            nodeReceipts,
            syncPeerPool,
            new SyncConfig { FixReceipts = true, FixReceiptsStartingBlock = 1, FixReceiptsLastBlock = 1 },
            MainnetSpecProvider.Instance,
            LimboLogs.Instance);

        await migration.Run(CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            syncPeerPool.DidNotReceiveWithAnyArgs().ReportBreachOfProtocol(default!, default, default!);
            Assert.That(nodeReceipts.Get(block), Has.Length.EqualTo(validReceipts.Length));
            Assert.That(nodeReceipts.Get(block)[0].Bloom, Is.EqualTo(validReceipts[0].Bloom));
        }
    }
}
