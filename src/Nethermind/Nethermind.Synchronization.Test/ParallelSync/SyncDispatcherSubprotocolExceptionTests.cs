// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Synchronization.ParallelSync;
using Nethermind.Synchronization.Peers;
using Nethermind.Synchronization.Peers.AllocationStrategies;
using Nethermind.Synchronization.Test.Mocks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Synchronization.Test.ParallelSync;

public class SyncDispatcherSubprotocolExceptionTests
{
    [Test, CancelAfter(10_000)]
    public async Task Should_log_subprotocol_dispatch_failure_as_compact_warning(CancellationToken cancellationToken)
    {
        CapturingLogManager logManager = new();
        SingleRequestFeed feed = new();
        ISyncPeerPool syncPeerPool = BuildPeerPool();
        SyncDispatcher<TestBatch> dispatcher = new(
            new TestSyncConfig(),
            feed,
            new ThrowingDownloader(new SubprotocolException("Receipt count mismatch with block transactions count. Block: 1 (0xabc), transactions: 2, receipts: 0")),
            syncPeerPool,
            new StaticPeerAllocationStrategyFactory<TestBatch>(FirstFree.Instance),
            logManager);

        Task dispatcherTask = dispatcher.Start(cancellationToken);
        feed.Activate();

        await dispatcherTask.WaitAsync(cancellationToken);

        Assert.That(feed.HandledResponse, Is.True);
        Assert.That(logManager.Logger.WarnMessages, Has.Count.EqualTo(1));
        Assert.That(logManager.Logger.DebugMessages, Has.None.Contains("Receipt count mismatch with block transactions count"));

        string warning = logManager.Logger.WarnMessages[0];
        Assert.That(warning, Does.Contain("peer protocol response rejected"));
        Assert.That(warning, Does.Contain("Receipt count mismatch with block transactions count"));
        Assert.That(warning, Does.Contain("Block: 1 (0xabc), transactions: 2, receipts: 0"));
        Assert.That(warning, Does.Not.Contain("Nethermind.Network.P2P.Subprotocols.SubprotocolException"));
        Assert.That(warning, Does.Not.Contain(" at Nethermind."));
    }

    private static ISyncPeerPool BuildPeerPool()
    {
        ISyncPeerPool syncPeerPool = Substitute.For<ISyncPeerPool>();
        PeerInfo peerInfo = new(new TestSyncPeer());
        SyncPeerAllocation allocation = new(peerInfo, AllocationContexts.All, new Lock());
        syncPeerPool.Allocate(
                Arg.Any<IPeerAllocationStrategy>(),
                Arg.Any<AllocationContexts>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(allocation));

        return syncPeerPool;
    }

    private sealed class TestSyncPeer : BaseSyncPeerMock
    {
        public override string ClientId { get; set; } = "Nethermind";
        public override UInt256? TotalDifficulty { get; set; } = UInt256.One;
    }

    private sealed class TestBatch
    {
    }

    private sealed class ThrowingDownloader(Exception exception) : ISyncDownloader<TestBatch>
    {
        public Task Dispatch(PeerInfo peerInfo, TestBatch request, CancellationToken cancellationToken) =>
            Task.FromException(exception);
    }

    private sealed class SingleRequestFeed : SyncFeed<TestBatch>
    {
        private bool _prepared;

        public bool HandledResponse { get; private set; }

        public override Task<TestBatch> PrepareRequest(CancellationToken token = default)
        {
            if (_prepared)
            {
                Finish();
                return Task.FromResult<TestBatch>(null!);
            }

            _prepared = true;
            return Task.FromResult(new TestBatch());
        }

        public override SyncResponseHandlingResult HandleResponse(TestBatch response, PeerInfo peer = null!)
        {
            HandledResponse = true;
            Finish();
            return SyncResponseHandlingResult.OK;
        }

        public override bool IsMultiFeed => false;

        public override AllocationContexts Contexts => AllocationContexts.All;

        public override void SyncModeSelectorOnChanged(SyncMode current)
        {
        }

        public override bool IsFinished => CurrentState == SyncFeedState.Finished;

        public override string FeedName => nameof(SingleRequestFeed);
    }

    private sealed class CapturingLogManager : ILogManager
    {
        public CapturingLogger Logger { get; } = new();

        public ILogger GetClassLogger<T>() => new(Logger);

        public ILogger GetLogger(string loggerName) => new(Logger);
    }

    private sealed class CapturingLogger : InterfaceLogger
    {
        public List<string> WarnMessages { get; } = [];

        public List<string> DebugMessages { get; } = [];

        public void Info(string text)
        {
        }

        public void Warn(string text) => WarnMessages.Add(text);

        public void Debug(string text) => DebugMessages.Add(text);

        public void Trace(string text)
        {
        }

        public void Error(string text, Exception? ex = null)
        {
        }

        public bool IsInfo => false;

        public bool IsWarn => true;

        public bool IsDebug => true;

        public bool IsTrace => false;

        public bool IsError => false;
    }
}
