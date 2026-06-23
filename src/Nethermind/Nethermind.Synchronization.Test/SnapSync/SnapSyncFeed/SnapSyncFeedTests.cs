// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
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

        Assert.That(feed.HandleResponse(response, peer), Is.EqualTo(SyncResponseHandlingResult.NoProgress));
    }

    [Test]
    public async Task Prepare_request_uses_configured_empty_request_delay()
    {
        ISnapProvider snapProvider = Substitute.For<ISnapProvider>();
        int calls = 0;
        snapProvider.IsFinished(out Arg.Any<SnapSyncBatch?>())
            .Returns(callInfo =>
            {
                callInfo[0] = null;
                calls++;
                return false;
            });

        SyncConfig syncConfig = new() { SyncDispatcherEmptyRequestDelayMs = 1 };
        Synchronization.SnapSync.SnapSyncFeed feed = new(snapProvider, LimboLogs.Instance, syncConfig);

        using CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(30));

        try
        {
            await feed.PrepareRequest(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.That(calls, Is.GreaterThan(2));
    }
}
