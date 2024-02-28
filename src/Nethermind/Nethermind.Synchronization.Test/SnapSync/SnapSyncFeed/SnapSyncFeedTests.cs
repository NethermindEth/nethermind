// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.State.Snap;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.SnapSync;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.SnapSync.SnapSyncFeed;

public class SnapSyncFeedTests
{
    [Test]
    public void WhenAccountRequestEmpty_ReturnNoProgress()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

        snapProvider.AddAccountRange(Arg.Any<AccountRange>(), Arg.Any<AccountsAndProofs>())
            .Returns(AddRangeResult.ExpiredRootHash);

        using SnapSyncBatch response = new();
        response.AccountRangeRequest = new AccountRange(Keccak.Zero, Keccak.Zero);
        response.AccountRangeResponse = new AccountsAndProofs();

        PeerInfo peer = new(Substitute.For<ISyncPeer>());

        feed.HandleResponse(response, peer).Should().Be(SyncResponseHandlingResult.NoProgress);
    }
}
