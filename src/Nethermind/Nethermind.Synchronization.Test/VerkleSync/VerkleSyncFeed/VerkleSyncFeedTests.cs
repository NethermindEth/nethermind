// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Verkle;
using Nethermind.Logging;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.VerkleSync;
using Nethermind.Verkle.Tree.Sync;
using Nethermind.Verkle.Tree.Utils;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.VerkleSync.VerkleSyncFeed;

public class VerkleSyncFeedTests
{
    [Test]
    public void WhenAccountRequestEmpty_ReturnNoProgress()
    {
        IVerkleSyncProvider verkleProvider = Substitute.For<IVerkleSyncProvider>();
        Synchronization.VerkleSync.VerkleSyncFeed feed = new(
            Substitute.For<ISyncModeSelector>(), verkleProvider, LimboLogs.Instance);

        verkleProvider.AddSubTreeRange(Arg.Any<SubTreeRange>(), Arg.Any<SubTreesAndProofs>())
            .Returns(AddRangeResult.ExpiredRootHash);

        VerkleSyncBatch response = new VerkleSyncBatch();
        response.SubTreeRangeRequest = new SubTreeRange(Pedersen.Zero, Stem.Zero.Bytes);
        response.SubTreeRangeResponse = new SubTreesAndProofs(Array.Empty<PathWithSubTree>(), Array.Empty<byte>());
        PeerInfo peer = new PeerInfo(Substitute.For<ISyncPeer>());

        feed.HandleResponse(response, peer).Should().Be(SyncResponseHandlingResult.NoProgress);
    }
}
