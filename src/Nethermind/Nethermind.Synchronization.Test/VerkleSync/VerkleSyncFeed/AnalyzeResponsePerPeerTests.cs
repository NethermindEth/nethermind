// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.VerkleSync;
using NSubstitute;
using NUnit.Framework;
using Nethermind.Synchronization.Peers;

namespace Nethermind.Synchronization.Test.VerkleSync.VerkleSyncFeed;

[TestFixture]
internal class AnalyzeResponsePerPeerTests
{
    [Test]
    public void Test01()
    {
        PeerInfo peer1 = new(null!);
        PeerInfo peer2 = new(null!);

        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        IVerkleSyncProvider verkleProvider = Substitute.For<IVerkleSyncProvider>();

        Synchronization.VerkleSync.VerkleSyncFeed feed = new(selector, verkleProvider, LimboLogs.Instance);

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

        var result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));
    }

    [Test]
    public void Test02()
    {
        PeerInfo peer1 = new(null!);
        PeerInfo peer2 = new(null!);

        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        IVerkleSyncProvider verkleProvider = Substitute.For<IVerkleSyncProvider>();

        Synchronization.VerkleSync.VerkleSyncFeed feed = new(selector, verkleProvider, LimboLogs.Instance);

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);

        var result = feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);

        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.LesserQuality));

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));
    }

    [Test]
    public void Test03()
    {
        PeerInfo peer1 = new(null!);
        PeerInfo peer2 = new(null!);

        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        IVerkleSyncProvider verkleProvider = Substitute.For<IVerkleSyncProvider>();

        Synchronization.VerkleSync.VerkleSyncFeed feed = new(selector, verkleProvider, LimboLogs.Instance);

        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer2);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        feed.AnalyzeResponsePerPeer(AddRangeResult.ExpiredRootHash, peer1);
        var result = feed.AnalyzeResponsePerPeer(AddRangeResult.DifferentRootHash, peer1);
        Assert.That(result, Is.EqualTo(SyncResponseHandlingResult.OK));

        verkleProvider.Received(1).UpdatePivot();
    }

    [Test]
    public void Test04()
    {
        PeerInfo peer1 = new(null!);

        ISyncModeSelector selector = Substitute.For<ISyncModeSelector>();
        IVerkleSyncProvider verkleProvider = Substitute.For<IVerkleSyncProvider>();

        Synchronization.VerkleSync.VerkleSyncFeed feed = new(selector, verkleProvider, LimboLogs.Instance);

        for (int i = 0; i < 200; i++)
        {
            feed.AnalyzeResponsePerPeer(AddRangeResult.OK, peer1);
        }
    }
}
