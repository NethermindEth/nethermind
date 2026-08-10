// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.FlatHistory;
using Nethermind.State;
using Nethermind.State.Flat.History;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test.FlatHistory;

public class NHistArchiveCloneSourceTests
{
    private static PeerInfo CreatePeer()
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        return new PeerInfo(syncPeer);
    }

    [Test]
    public async Task GetHistoryRowsAsync_ForwardsOneCallToOneCallAndPreservesEntriesAndCursor()
    {
        HistoryRowEntry entry = new(new byte[] { 1, 2, 3 }, new byte[] { 4, 5 });
        byte[] nextCursor = [9, 9];

        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetHistoryRows(HistoryRowColumn.AccountHistory, Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Returns(new NHistRowsPage([entry], nextCursor, Refused: false));

        NHistArchiveCloneSource source = new(CreatePeer(), syncPeer, rowFormatVersion: 3, watermark: 100);

        ArchiveCloneRowPage page = await source.GetHistoryRowsAsync(HistoryRowColumn.AccountHistory, [0], [0xFF], cursor: null, CancellationToken.None);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(page.Entries, Has.Count.EqualTo(1));
            Assert.That(page.Entries[0], Is.EqualTo(entry));
            Assert.That(page.NextCursor, Is.EqualTo(nextCursor));
            Assert.That(page.Refused, Is.False);
        }
    }

    [Test]
    public async Task GetHistoryRowsAsync_PreservesRefusedSignalEndToEnd()
    {
        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.GetHistoryRows(Arg.Any<HistoryRowColumn>(), Arg.Any<byte[]>(), Arg.Any<byte[]>(), Arg.Any<byte[]?>(), Arg.Any<CancellationToken>())
            .Returns(new NHistRowsPage([], null, Refused: true));

        NHistArchiveCloneSource source = new(CreatePeer(), syncPeer, rowFormatVersion: 3, watermark: 100);

        ArchiveCloneRowPage page = await source.GetHistoryRowsAsync(HistoryRowColumn.Code, [0], [0xFF], cursor: null, CancellationToken.None);

        Assert.That(page.Refused, Is.True, "a peer's refusal must propagate all the way to ArchiveCloneImporter, which fails closed on it - never silently treated as an empty-but-successful page");
    }

    [Test]
    public void FromPeer_DerivesWatermarkFromFirstServedScope()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 0, 12345);
        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.PeerServedScopes.Returns(new[] { scope });
        syncPeer.PeerRowFormatVersion.Returns((byte)3);

        NHistArchiveCloneSource source = NHistArchiveCloneSource.FromPeer(CreatePeer(), syncPeer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Watermark, Is.EqualTo(12345UL));
            Assert.That(source.RowFormatVersion, Is.EqualTo((byte)3));
        }
    }

    [Test]
    public void FromPeer_WhenPeerHasNoServedScopesYet_WatermarkIsZero()
    {
        INHistSyncPeer syncPeer = Substitute.For<INHistSyncPeer>();
        syncPeer.PeerServedScopes.Returns([]);

        NHistArchiveCloneSource source = NHistArchiveCloneSource.FromPeer(CreatePeer(), syncPeer);

        Assert.That(source.Watermark, Is.EqualTo(0UL));
    }
}
