// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Blockchain.Synchronization;
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

        Assert.That(feed.HandleResponse(response, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
    }

    /// <summary>RefreshAccounts maps every verification failure to InvalidProof itself, so it can only
    /// throw outside that guard, hence a substitute rather than a real provider.</summary>
    [Test]
    public void WhenRefreshAccountsThrows_ReleasesTheRequestForRetry()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance);

        snapProvider.RefreshAccounts(Arg.Any<AccountsToRefreshRequest>(), Arg.Any<AccountsAndProofs>())
            .Returns(_ => throw new IOException("state backend unavailable"));

        using SnapSyncBatch batch = new()
        {
            AccountsToRefreshRequest = new AccountsToRefreshRequest { RootHash = Keccak.Zero, Paths = ArrayPoolList<AccountWithStorageStartingHash>.Empty() },
            AccountsToRefreshResponse = new AccountsAndProofs()
        };

        Assert.That(() => feed.HandleResponse(batch, null), Throws.InstanceOf<IOException>());

        snapProvider.Received(1).ReleaseRequest(batch, responseHandled: false);
    }
}
