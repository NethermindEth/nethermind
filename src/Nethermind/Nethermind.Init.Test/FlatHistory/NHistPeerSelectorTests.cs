// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Init.FlatHistory;
using Nethermind.Network.Contract.P2P;
using Nethermind.State;
using Nethermind.Stats.Model;
using Nethermind.Synchronization.Peers;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Init.Test.FlatHistory;

public class NHistPeerSelectorTests
{
    private static PeerInfo CreatePeer(PublicKey id, INHistSyncPeer? nhist)
    {
        ISyncPeer syncPeer = Substitute.For<ISyncPeer>();
        syncPeer.Node.Returns(new Node(id, "127.0.0.1", 30303));
        syncPeer.TryGetSatelliteProtocol(Protocol.NHist, out Arg.Any<INHistSyncPeer>())
            .Returns(x =>
            {
                x[1] = nhist!;
                return nhist is not null;
            });

        return new PeerInfo(syncPeer);
    }

    private static INHistSyncPeer CreateNHistPeer(HistoryServingScope[] servedScopes, bool supportsFullClone, byte rowFormatVersion)
    {
        INHistSyncPeer nhist = Substitute.For<INHistSyncPeer>();
        nhist.PeerServedScopes.Returns(servedScopes);
        nhist.PeerSupportsFullClone.Returns(supportsFullClone);
        nhist.PeerRowFormatVersion.Returns(rowFormatVersion);
        return nhist;
    }

    [Test]
    public void TryGetEligibleImportPeer_WhenPeerNeverNegotiatedNHist_IsSkipped()
    {
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist: null);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        Assert.That(selector.TryGetEligibleImportPeer(NHistPeerSelector.NoExclusions, out _, out _), Is.False,
            "a peer with no nhist1 satellite protocol at all must never be selected");
    }

    [Test]
    public void TryGetEligibleImportPeer_WhenPeerHasNotExchangedStatusYet_IsSkipped()
    {
        INHistSyncPeer nhist = CreateNHistPeer([], supportsFullClone: false, rowFormatVersion: 3);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        Assert.That(selector.TryGetEligibleImportPeer(NHistPeerSelector.NoExclusions, out _, out _), Is.False,
            "an empty PeerServedScopes means the status handshake has not completed yet - not eligible");
    }

    [Test]
    public void TryGetEligibleImportPeer_WhenPeerServesAnyScope_IsEligibleEvenWithoutFullClone()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 100, 200);
        INHistSyncPeer nhist = CreateNHistPeer([scope], supportsFullClone: false, rowFormatVersion: 3);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(selector.TryGetEligibleImportPeer(NHistPeerSelector.NoExclusions, out PeerInfo selectedPeer, out INHistSyncPeer selected), Is.True,
                "a windowed peer serving a bounded scope is a perfectly valid import source");
            Assert.That(selectedPeer, Is.Not.Null);
            Assert.That(selected, Is.SameAs(nhist));
        }
    }

    [Test]
    public void TryGetEligibleImportPeer_WhenPeerExcluded_IsSkipped()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 0, 100);
        INHistSyncPeer nhist = CreateNHistPeer([scope], supportsFullClone: false, rowFormatVersion: 3);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);
        HashSet<PublicKey> excluded = [TestItem.PublicKeyA];

        Assert.That(selector.TryGetEligibleImportPeer(excluded, out _, out _), Is.False,
            "an excluded (e.g. already-banned) peer must never be reselected");
    }

    [Test]
    public void TryGetEligibleCloneSource_WhenPeerIsWindowedOnly_IsExcluded()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 100, 200);
        INHistSyncPeer nhist = CreateNHistPeer([scope], supportsFullClone: false, rowFormatVersion: 3);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        Assert.That(selector.TryGetEligibleCloneSource(3, NHistPeerSelector.NoExclusions, out _, out _), Is.False,
            "a peer that only serves a bounded retention window can never source a full clone, regardless of row format");
    }

    [Test]
    public void TryGetEligibleCloneSource_WhenRowFormatMismatches_IsExcluded()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 0, 200);
        INHistSyncPeer nhist = CreateNHistPeer([scope], supportsFullClone: true, rowFormatVersion: 2);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        Assert.That(selector.TryGetEligibleCloneSource(3, NHistPeerSelector.NoExclusions, out _, out _), Is.False,
            "no transcoding is supported on the wire - a row format mismatch must exclude the peer, never silently clone the wrong shape");
    }

    [Test]
    public void TryGetEligibleCloneSource_ReportsPeersWithoutTheSatelliteAsOneAggregateLine()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 0, 200);
        INHistSyncPeer windowedOnly = CreateNHistPeer([scope], supportsFullClone: false, rowFormatVersion: 3);
        PeerInfo[] peers = [
            CreatePeer(TestItem.PublicKeyA, nhist: null),
            CreatePeer(TestItem.PublicKeyB, nhist: null),
            CreatePeer(TestItem.PublicKeyC, windowedOnly)
        ];
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns(peers);

        NHistPeerSelector selector = new(pool);
        List<string> reasons = [];

        Assert.That(selector.TryGetEligibleCloneSource(3, NHistPeerSelector.NoExclusions, out _, out _, reasons.Add), Is.False);
        Assert.That(reasons, Has.Exactly(1).Contains("2 of 3 connected peers do not advertise the nhist satellite protocol"),
            "peers without the satellite must be summarized in a single line, not dumped one line per peer");
        Assert.That(reasons, Has.Exactly(1).Contains("SupportsFullClone=false"),
            "a peer that negotiated nhist but cannot source a clone is worth an individual line");
    }

    [Test]
    public void TryGetEligibleCloneSource_WhenFullCloneAndFormatMatch_IsEligible()
    {
        HistoryServingScope scope = new(ValueKeccak.Zero, ValueKeccak.MaxValue, 0, 200);
        INHistSyncPeer nhist = CreateNHistPeer([scope], supportsFullClone: true, rowFormatVersion: 3);
        PeerInfo peer = CreatePeer(TestItem.PublicKeyA, nhist);
        ISyncPeerPool pool = Substitute.For<ISyncPeerPool>();
        pool.InitializedPeers.Returns([peer]);

        NHistPeerSelector selector = new(pool);

        Assert.That(selector.TryGetEligibleCloneSource(3, NHistPeerSelector.NoExclusions, out _, out INHistSyncPeer selected), Is.True);
        Assert.That(selected, Is.SameAs(nhist));
    }
}
