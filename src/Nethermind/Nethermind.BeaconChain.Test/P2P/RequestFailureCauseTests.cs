// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerSessionNodes;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>
/// A failed request names what ended it (the request budget and what it was waiting for, a bound of the read, the session closing, or our own
/// cancellation), so a timeout, a lost peer and a shutdown can be told apart in the log and in the peer's failure accounting.
/// </summary>
public class RequestFailureCauseTests
{
    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;
    private const string PeerAddress = "/ip4/10.0.0.1/tcp/9000/p2p/16Uiu2HAmPeer";
    // Stays under the request-failure limit, so the limit takes the peer under test out of selection.
    private const string UsablePeerAddress = "/ip4/10.0.0.2/tcp/9000/p2p/16Uiu2HAmUsable";

    public enum ChannelRequestEnd { Budget, Disconnect, Reset, AlreadyDropped, CallerCancellation }

    [TestCase(ChannelRequestEnd.Budget)]
    [TestCase(ChannelRequestEnd.Disconnect)]
    [TestCase(ChannelRequestEnd.Reset)]
    [TestCase(ChannelRequestEnd.AlreadyDropped)]
    [TestCase(ChannelRequestEnd.CallerCancellation)]
    [CancelAfter(30_000)]
    public async Task Channel_requests_preserve_budget_disconnect_and_caller_cancellation_causes(ChannelRequestEnd end, CancellationToken token)
    {
        await using BeaconP2P node = CreateHost(end == ChannelRequestEnd.Budget ? TimeSpan.FromMilliseconds(300) : TimeSpan.FromSeconds(20));
        await node.StartAsync(token);
        LocalPeer.Session session = AddWedgedSession(node);
        RequestTiming timing = new();
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
        if (end == ChannelRequestEnd.AlreadyDropped) await session.DisconnectAsync();
        if (end == ChannelRequestEnd.CallerCancellation) stopping.CancelAfter(TimeSpan.FromMilliseconds(200));
        long startedAt = Stopwatch.GetTimestamp();
        Task? request = end is ChannelRequestEnd.Disconnect or ChannelRequestEnd.Reset
            ? node.RequestDataColumnSidecarsByRootAsync(session, [new DataColumnsByRootIdentifier { BlockRoot = Hash256.Zero, Columns = [1] }], token, timing) : null;
        if (end is ChannelRequestEnd.Disconnect or ChannelRequestEnd.Reset)
        {
            startedAt = Stopwatch.GetTimestamp();
            if (end == ChannelRequestEnd.Reset)
            {
                lock (node.LocalPeerForTest!.Sessions) node.LocalPeerForTest.Sessions.Clear();
            }
            else await session.DisconnectAsync();
        }
        Exception? failure = request is not null ? Assert.CatchAsync(async () => await request)
            : Assert.CatchAsync(() => end == ChannelRequestEnd.AlreadyDropped
                ? node.RequestBlocksByRangeAsync(session, 1, 1, token)
                : node.RequestBlocksByRootAsync(session, [Hash256.Zero], end == ChannelRequestEnd.CallerCancellation ? stopping.Token : token, end == ChannelRequestEnd.Budget ? timing : null));
        using (Assert.EnterMultipleScope())
        {
            if (end == ChannelRequestEnd.Budget)
            {
                Assert.That(failure, Is.TypeOf<ReqRespTimeoutException>());
                Assert.That(failure!.Message, Does.Match(@"^timed out after 1\.\d s waiting for the channel to open: the request budget ran out$"));
                Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(PeerFailureReason.RequestFailed));
                Assert.That(timing.ToString(), Does.StartWith("channel open not reached, first chunk not reached, total "));
            }
            else if (end == ChannelRequestEnd.CallerCancellation)
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>().And.Not.InstanceOf<TimeoutException>());
            else
            {
                Assert.That(failure, Is.TypeOf<IOException>());
                Assert.That(Stopwatch.GetElapsedTime(startedAt), Is.LessThan(TimeSpan.FromSeconds(5)));
                if (end == ChannelRequestEnd.AlreadyDropped) Assert.That(failure!.Message, Does.StartWith("peer disconnected after "));
                else
                {
                    Assert.That(failure!.Message, Does.Match(@"^peer disconnected after \d+(\.\d)? s waiting for the channel to open: its libp2p session closed$"));
                    Assert.That(PeerFailureClassifier.Classify(failure), Is.EqualTo(PeerFailureReason.SessionClosed));
                }
            }
        }
    }

    [Test]
    public async Task A_bound_of_the_protocols_own_read_is_passed_on_unwrapped()
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(default!, default).ReturnsForAnyArgs(
            Task.FromException<IReadOnlyList<ForkedSignedBeaconBlock>>(new AggregateException(new TimeoutException("timed out after 10 s reading chunk 3"))));
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(15));

        TimeoutException? cut = Assert.ThrowsAsync<TimeoutException>(() => node.RequestBlocksByRootAsync(session, [Hash256.Zero], default));

        Assert.That(cut!.Message, Is.EqualTo("timed out after 10 s reading chunk 3"));
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_timed_request_over_a_live_session_reports_when_its_channel_opened_and_its_chunks(CancellationToken token)
    {
        Node server = Create();
        Node client = Create();
        await using (server.P2P)
        await using (client.P2P)
        {
            await server.P2P.StartAsync(token);
            await client.P2P.StartAsync(token);
            ISession session = await client.P2P.DialPeerAsync(LoopbackAddress(server.P2P), token);
            RequestTiming status = new();
            RequestTiming blocks = new();

            await client.P2P.RequestStatusAsync(session, token, status);
            await client.P2P.RequestBlocksByRootAsync(session, [Hash256.Zero], token, blocks);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(status.ToString(), Does.Match(@"^channel open \d+ ms, first chunk \d+ ms, total \d+ ms, 1 chunks$"));
                Assert.That(blocks.ToString(), Does.Match(@"^channel open \d+ ms, first chunk not reached, total \d+ ms, 0 chunks$"), "an unknown root is answered with no chunks");
            }
        }
    }

    public enum SlotCancellation { Queued, BeforeChannelOpen, WhileChannelOpen }

    // Consensus-specs v1.7.0-beta.2 req/resp requesting side: the requester MUST NOT make more than MAX_CONCURRENT_REQUESTS concurrent requests with the same protocol ID.
    [TestCase(SlotCancellation.Queued)]
    [TestCase(SlotCancellation.BeforeChannelOpen)]
    [TestCase(SlotCancellation.WhileChannelOpen)]
    [CancelAfter(30_000)]
    public async Task Request_slots_preserve_protocol_bounds_and_cancelled_channel_ownership(SlotCancellation cancellation, CancellationToken token)
    {
        LevelCapturingLogManager logs = new();
        HeldSession held = new(opensChannels: cancellation == SlotCancellation.WhileChannelOpen);
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = cancellation == SlotCancellation.Queued
                ? new(node.P2P, node.Config, node.StatusHolder, logs) : node.CreatePeerManager();
            IBeaconSyncPeer peer = manager.AddPeerForTest(held.Session, PeerAddress);
            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<IReadOnlyList<DataColumnSidecar>> first = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(0), cancellation == SlotCancellation.Queued ? token : stopping.Token);
            Task<IReadOnlyList<DataColumnSidecar>> second = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(1), token);
            Task<IReadOnlyList<DataColumnSidecar>> third = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(2), token);
            Task<IReadOnlyList<ForkedSignedBeaconBlock>>? otherProtocol = null;
            bool openedBeforeReadingStopped = false;
            if (cancellation == SlotCancellation.Queued)
            {
                int dialedAtOnce = held.ColumnDials.Count;
                otherProtocol = peer.RequestBlocksByRootAsync([Hash256.Zero], token);
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(dialedAtOnce, Is.EqualTo(2));
                    Assert.That(held.BlockDials, Is.EqualTo(1), "another protocol has its own slots");
                    Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.EqualTo(4), "a slot wait counts as in flight");
                }
                Task<IReadOnlyList<DataColumnSidecar>> abandoned = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(3), stopping.Token);
                await stopping.CancelAsync();
                Assert.CatchAsync<OperationCanceledException>(() => abandoned);
                Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.EqualTo(4), "cancelled slot wait releases its count");
                Assert.That(logs.Lines.Where(static l => l.Text.Contains("request not sent, cancelled by this node")).Select(static l => l.Text), Has.Some.Contains("requests in flight 4,"));
                held.ColumnDials[0].SetResult(new ForkedDataColumnSidecars([], []));
                await first.WaitAsync(token);
            }
            else
            {
                await stopping.CancelAsync();
                Assert.CatchAsync<OperationCanceledException>(() => first);
                if (cancellation == SlotCancellation.WhileChannelOpen)
                {
                    openedBeforeReadingStopped = await EventuallyAsync(() => held.ColumnDials.Count == 3, TimeSpan.FromMilliseconds(500), token);
                    held.Exchanges[0].Dispose();
                }
            }
            await WaitUntilAsync(() => held.ColumnDials.Count == 3, token);
            if (cancellation == SlotCancellation.BeforeChannelOpen)
                Assert.Throws<OperationCanceledException>(() => RequestTiming.Open(held.ColumnRequests[0]), "an abandoned request is refused when its channel opens late");
            foreach (TaskCompletionSource<ForkedDataColumnSidecars> dial in held.ColumnDials) dial.TrySetResult(new ForkedDataColumnSidecars([], []));
            await Task.WhenAll(second, third).WaitAsync(token);
            if (otherProtocol is not null)
            {
                held.BlockDial.SetResult([]);
                await otherProtocol.WaitAsync(token);
            }
            if (cancellation == SlotCancellation.WhileChannelOpen)
                Assert.That(openedBeforeReadingStopped, Is.False, "a third channel cannot open while two are still open");
            else Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.Zero);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_request_waiting_for_a_slot_ends_when_its_session_closes_before_the_running_protocols_end(CancellationToken token)
    {
        Node node = Create();
        await using (node.P2P)
        {
            await node.P2P.StartAsync(token);
            LocalPeer.Session session = AddWedgedSession(node.P2P);
            IBeaconSyncPeer peer = node.CreatePeerManager().AddPeerForTest(session, PeerAddress);
            Task<IReadOnlyList<DataColumnSidecar>> first = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(0), token);
            Task<IReadOnlyList<DataColumnSidecar>> second = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(1), token);
            BlockingCollection<UpgradeOptions> requests = (BlockingCollection<UpgradeOptions>)typeof(LocalPeer.Session)
                .GetField("SubDialRequests", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
            RequestTiming.Exchange[] running = [.. requests.Select(static request => RequestTiming.Open(((DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>)request.Argument!).Request))];
            Assert.That(running, Has.Length.EqualTo(2));
            Task<IReadOnlyList<DataColumnSidecar>> queued = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(2), token);
            try
            {
                await session.DisconnectAsync();

                IOException? lost = Assert.ThrowsAsync<IOException>(async () => await queued.WaitAsync(TimeSpan.FromSeconds(2), token));

                Assert.That(lost!.Message, Does.Contain("waiting for a request slot"));
            }
            finally
            {
                foreach (RequestTiming.Exchange exchange in running)
                {
                    exchange.Dispose();
                }
            }

            Assert.CatchAsync<IOException>(() => Task.WhenAll(first, second, queued));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_timeout_while_no_request_to_any_peer_was_answered_is_not_counted_against_the_peer(CancellationToken token)
    {
        LevelCapturingLogManager logs = new();
        HeldSession silentSession = new();
        HeldSession answeringSession = new();
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, logs);
            IBeaconSyncPeer silent = manager.AddPeerForTest(silentSession.Session, PeerAddress);
            IBeaconSyncPeer answering = manager.AddPeerForTest(answeringSession.Session, "/ip4/10.0.0.2/tcp/9000/p2p/16Uiu2HAmIdle");

            await FailWithoutAnswerAsync(silent, silentSession, 0, token);
            int afterStall = PeerManager.ConsecutiveFailuresForTest(silent);

            Task<IReadOnlyList<DataColumnSidecar>> failing = silent.RequestDataColumnSidecarsByRootAsync(Identifiers(1), token);
            Task<IReadOnlyList<DataColumnSidecar>> answered = answering.RequestDataColumnSidecarsByRootAsync(Identifiers(2), token);
            answeringSession.ColumnDials[0].SetResult(new ForkedDataColumnSidecars([], []));
            await answered.WaitAsync(token);
            await FailAsync(failing, silent, silentSession.ColumnDials[1]);
            int afterAnswerElsewhere = PeerManager.ConsecutiveFailuresForTest(silent);

            for (int i = 2; i < 2 + 8; i++)
            {
                await FailWithoutAnswerAsync(silent, silentSession, i, token);
            }

            int afterGrace = PeerManager.ConsecutiveFailuresForTest(silent);
            await FailWithoutAnswerAsync(silent, silentSession, 10, token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(afterStall, Is.Zero, "nothing answered while it waited, so the stall is taken as this node's");
                Assert.That(afterAnswerElsewhere, Is.EqualTo(1), "another peer answered while it waited, so the silence is this peer's");
                Assert.That(afterGrace, Is.EqualTo(2), "seven more are excused, the eighth unanswered in a row is not");
                Assert.That(PeerManager.ConsecutiveFailuresForTest(silent), Is.EqualTo(3));
                Assert.That(logs.Lines.Select(static l => l.Text), Has.Some.Contains("not counted against the peer as no request to any peer was answered meanwhile"));
            }
        }
    }

    /// <summary>A by-range reply cut by a timeout after some chunks carries what it delivered; the timeout inside must still be excused while no request of ours was answered.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_timeout_after_some_chunks_while_no_request_was_answered_is_not_counted_against_the_peer([Values] bool columns, CancellationToken token)
    {
        ISession session = Substitute.For<ISession>();
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(5, Hash256.Zero));
        session.DialAsync<BeaconBlocksByRangeProtocolV2, BeaconBlocksByRangeDial, IReadOnlyList<ForkedSignedBeaconBlock>>(default, default).ReturnsForAnyArgs(call =>
        {
            call.Arg<BeaconBlocksByRangeDial>().OnBlock!(block);
            return Task.FromCanceled<IReadOnlyList<ForkedSignedBeaconBlock>>(new CancellationToken(true));
        });
        session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(call =>
        {
            call.Arg<DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>>().OnSidecar!(DataColumnSidecarTestFixture.BuildValidSidecar(3, 5, blobCount: 1));
            return Task.FromCanceled<ForkedDataColumnSidecars>(new CancellationToken(true));
        });
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance);
            IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress);

            Exception? failure = columns
                ? Assert.CatchAsync(async () => await peer.RequestDataColumnSidecarsByRangeAsync(5, 1, [3], token))
                : Assert.CatchAsync(async () => await peer.RequestBlocksByRangeAsync(5, 2, token));
            peer.ReportFailure(PeerFailureClassifier.Classify(failure!), failure!.Message);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(failure, columns ? Is.InstanceOf<PartialSidecarsException>() : Is.InstanceOf<PartialBlocksException>(), "fixture: the reply delivered a chunk before the timeout");
                Assert.That(PeerManager.ConsecutiveFailuresForTest(peer), Is.Zero);
            }
        }
    }

    [TestCase(typeof(ReqRespTimeoutException), false, 2, false, ExpectedResult = true)]
    [TestCase(typeof(OperationCanceledException), false, 1, false, ExpectedResult = true)]
    [TestCase(typeof(ReqRespTimeoutException), false, 3, false, ExpectedResult = false)]
    [TestCase(typeof(ReqRespTimeoutException), true, 1, false, ExpectedResult = false)]
    [TestCase(typeof(ReqRespTimeoutException), false, 1, true, ExpectedResult = false)]
    [TestCase(typeof(IOException), false, 1, false, ExpectedResult = false)]
    public bool A_peer_that_only_times_out_is_kept_at_or_below_the_peer_floor(Type failure, bool channelNeverOpened, int connected, bool violated)
    {
        Node node = Create();
        node.Config.MinPeerCount = 2;
        PeerManager manager = node.CreatePeerManager();
        Exception e = failure == typeof(ReqRespTimeoutException) ? new ReqRespTimeoutException("timed out after 16 s waiting for the first chunk") { ChannelNeverOpened = channelNeverOpened }
            : failure == typeof(IOException) ? new IOException("peer disconnected after 1 s: its libp2p session closed")
            : new OperationCanceledException();
        return manager.IsKeptAtPeerFloor(e, connected, violated);
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Concurrent_health_timeouts_do_not_drop_peers_below_the_floor(CancellationToken token)
    {
        Node node = Create();
        await using (node.P2P)
        {
            using ManualResetEventSlim releaseFirst = new();
            TaskCompletionSource firstFloorRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource bothPingsFailed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int floorReads = 0;
            int failedPings = 0;
            IBeaconChainConfig config = Substitute.For<IBeaconChainConfig>();
            config.MaxPeerCount.Returns(8);
            config.TargetPeerCount.Returns(4);
            config.MinPeerCount.Returns(_ =>
            {
                if (Interlocked.Increment(ref floorReads) == 1)
                {
                    firstFloorRead.TrySetResult();
                    releaseFirst.Wait(token);
                }

                return 2;
            });
            PeerManager manager = new(node.P2P, config, node.StatusHolder, LimboLogs.Instance);
            List<IBeaconSyncPeer> peers = [];
            List<TaskCompletionSource<ulong>> timeouts = [];
            for (int i = 0; i < 3; i++)
            {
                ISession session = Substitute.For<ISession>();
                session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default)
                    .ReturnsForAnyArgs(Task.FromResult(node.StatusHolder.CurrentStatus));
                TaskCompletionSource<ulong> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
                if (i < 2) timeouts.Add(pending);
                Task<ulong> response = i == 2 ? Task.FromResult(0UL) : pending.Task;
                session.DialAsync<Eth2PingProtocol, ulong, ulong>(default, default).ReturnsForAnyArgs(_ =>
                {
                    if (!response.IsCompleted && Interlocked.Increment(ref failedPings) == 2)
                    {
                        bothPingsFailed.TrySetResult();
                    }

                    return response;
                });
                IBeaconSyncPeer peer = manager.AddPeerForTest(session, $"/ip4/10.0.0.{i + 1}/tcp/9000/p2p/16Uiu2HAmPeer{i}");
                peers.Add(peer);
            }

            await InvokeStatusAsync(manager, peers[2], "UpdateStatusAsync", token);
            // Seed the independent health-timeout budget, which sync failures no longer consume.
            foreach (IBeaconSyncPeer peer in peers.Take(2))
            {
                for (int failures = 0; failures < 7; failures++)
                {
                    await manager.HandleHealthFailureAsync(peer, new TimeoutException("request timed out"), 0, token);
                }
            }

            Task maintenance = manager.RunMaintenanceRoundAsync(token);
            try
            {
                await bothPingsFailed.Task.WaitAsync(token);
                await InvokeStatusAsync(manager, peers[2], "UpdateStatusAsync", token);
                foreach (TaskCompletionSource<ulong> timeout in timeouts) timeout.SetException(new TimeoutException("request timed out"));
                await firstFloorRead.Task.WaitAsync(token);
                await EventuallyAsync(() => manager.PeerCount == 2, TimeSpan.FromSeconds(2), token);
            }
            finally
            {
                releaseFirst.Set();
            }

            await maintenance.WaitAsync(token);
            Assert.That(manager.PeerCount, Is.EqualTo(2));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Each_request_ends_with_a_debug_line_naming_its_protocol_peer_timing_and_outcome(CancellationToken token)
    {
        LevelCapturingLogManager logs = new();
        HeldSession held = new();
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, logs);
            IBeaconSyncPeer peer = manager.AddPeerForTest(held.Session, PeerAddress);

            Task<IReadOnlyList<DataColumnSidecar>> served = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(0), token);
            held.ColumnDials[0].SetResult(new ForkedDataColumnSidecars([], []));
            await served.WaitAsync(token);

            Task<IReadOnlyList<DataColumnSidecar>> unanswered = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(1), token);
            held.ColumnDials[1].TrySetCanceled(CancellationToken.None);
            Assert.CatchAsync(() => unanswered);

            using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task<IReadOnlyList<DataColumnSidecar>> cancelled = peer.RequestDataColumnSidecarsByRootAsync(Identifiers(2), stopping.Token);
            await stopping.CancelAsync();
            held.ColumnDials[2].TrySetCanceled(stopping.Token);
            Assert.CatchAsync<OperationCanceledException>(() => cancelled);
        }

        string[] lines = [.. logs.Lines.Where(static l => l.Level == "Debug" && l.Text.StartsWith(PeerManager.RequestName.ColumnsByRoot, StringComparison.Ordinal)).Select(static l => l.Text)];
        string timing = @"requests in flight 1, slot wait \d+ ms, channel open not reached, first chunk not reached, total \d+ ms, 0 chunks";
        using (Assert.EnterMultipleScope())
        {
            Assert.That(lines, Has.Length.EqualTo(3));
            Assert.That(lines.ElementAtOrDefault(0), Does.Match($"^Data-column-sidecars-by-root to {PeerAddress}: {timing}, served$"));
            Assert.That(lines.ElementAtOrDefault(1), Does.Match($"^Data-column-sidecars-by-root to {PeerAddress}: {timing}, ended after \\d+(\\.\\d)? s waiting for the channel to open without an answer: the libp2p layer cancelled the exchange, not counted against the peer as no request to any peer was answered meanwhile$"));
            Assert.That(lines.ElementAtOrDefault(2), Does.Match($"^Data-column-sidecars-by-root to {PeerAddress}: {timing}, cancelled by this node$"));
            Assert.That(logs.Lines.Select(static l => l.Text), Has.None.Contains("Exception"));
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_peer_with_a_request_in_flight_is_listed_after_an_idle_one_with_a_lower_head(CancellationToken token)
    {
        HeldSession busySession = new();
        HeldSession idleSession = new();
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            StatusMessageV2 ahead = Status;
            ahead.HeadSlot += 10;
            IBeaconSyncPeer busy = manager.AddPeerForTest(busySession.Session, PeerAddress, ahead);
            IBeaconSyncPeer idle = manager.AddPeerForTest(idleSession.Session, "/ip4/10.0.0.2/tcp/9000/p2p/16Uiu2HAmIdle", Status);
            string[] before = [.. manager.GetBestPeers(0).Select(static p => p.Id)];

            Task<IReadOnlyList<DataColumnSidecar>> pending = busy.RequestDataColumnSidecarsByRootAsync(Identifiers(0), token);
            string[] during = [.. manager.GetBestPeers(0).Select(static p => p.Id)];
            busySession.ColumnDials[0].SetResult(new ForkedDataColumnSidecars([], []));
            await pending.WaitAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(before, Is.EqualTo(new[] { busy.Id, idle.Id }), "test setup: the higher head is first while both are idle");
                Assert.That(during, Is.EqualTo(new[] { idle.Id, busy.Id }));
                Assert.That(manager.GetBestPeers(0).Select(static p => p.Id), Is.EqualTo(new[] { busy.Id, idle.Id }));
            }
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Status_refresh_timeouts_never_count_toward_a_drop(CancellationToken token)
    {
        ISession session = Substitute.For<ISession>();
        session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(_ =>
            Task.FromException<StatusMessageV2>(new ReqRespTimeoutException("timed out waiting for the response")));
        session.DialAsync<StatusProtocolV1, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(_ =>
            Task.FromException<StatusMessageV2>(new ReqRespTimeoutException("timed out waiting for the response")));
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress, Status);
            manager.CountEveryTimeoutForTest();

            for (int i = 0; i < 9; i++)
            {
                await InvokeStatusAsync(manager, peer, "RefreshStatusAsync", token);
            }

            using (Assert.EnterMultipleScope())
            {
                Assert.That(PeerManager.ConsecutiveFailuresForTest(peer), Is.Zero);
                Assert.That(PeerManager.FailuresReportedForTest(peer), Is.Zero);
                Assert.That(manager.PeerCount, Is.EqualTo(1));
                Assert.That(manager.GetBestPeers(0), Has.Count.EqualTo(1));
                Assert.That(manager.IsBannedForTest(peer.Id), Is.False);
            }
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Status_refresh_waits_for_the_same_slots_as_a_health_status_request(CancellationToken token)
    {
        ISession session = Substitute.For<ISession>();
        List<TaskCompletionSource<StatusMessageV2>> answers = [];
        session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(_ =>
        {
            TaskCompletionSource<StatusMessageV2> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (answers)
            {
                answers.Add(answer);
            }

            return answer.Task;
        });
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress, Status);
            Task first = InvokeStatusAsync(manager, peer, "UpdateStatusAsync", token);
            Task second = InvokeStatusAsync(manager, peer, "UpdateStatusAsync", token);
            Task refresh = InvokeStatusAsync(manager, peer, "RefreshStatusAsync", token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(answers, Has.Count.EqualTo(2));
                Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.EqualTo(3));
            }

            answers[0].SetResult(Status);
            await first.WaitAsync(token);
            await WaitUntilAsync(() =>
            {
                lock (answers)
                {
                    return answers.Count == 3;
                }
            }, token);
            answers[1].SetResult(Status);
            answers[2].SetResult(Status);
            await Task.WhenAll(second, refresh).WaitAsync(token);
            Assert.That(PeerManager.RequestsInFlightForTest(peer), Is.Zero);
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Established_session_reads_wait_for_the_library_collection_lock(CancellationToken token)
    {
        await using BeaconP2P node = CreateHost(TimeSpan.FromSeconds(15));
        await node.StartAsync(token);
        using ManualResetEventSlim reading = new();
        Task read;
        bool completedWhileLocked;
        lock (node.LocalPeerForTest!.Sessions)
        {
            read = Task.Run(() =>
            {
                reading.Set();
                return node.TryGetEstablishedSession(node.LocalPeerId!, out _);
            }, token);
            Assert.That(reading.Wait(TimeSpan.FromSeconds(5), token), Is.True);
            completedWhileLocked = SpinWait.SpinUntil(() => read.IsCompleted, TimeSpan.FromMilliseconds(200));
        }

        await read.WaitAsync(token);
        Assert.That(completedWhileLocked, Is.False, "the library can be modifying the collection while it holds the lock");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Status_refresh_checks_finalized_roots_and_keeps_status_fresh([Values] bool conflicting, CancellationToken token)
    {
        Node node = Create();
        await using (node.P2P)
        {
            ManualTimestamper clock = new();
            StatusMessageV2 local = node.StatusHolder.CurrentStatus;
            local.FinalizedEpoch = 1;
            local.FinalizedRoot = Keccak.Compute("local finalized");
            StatusMessageV2 updated = new()
            {
                ForkDigest = local.ForkDigest,
                FinalizedEpoch = local.FinalizedEpoch,
                FinalizedRoot = conflicting ? Keccak.Compute("other finalized") : local.FinalizedRoot,
                HeadSlot = local.HeadSlot + 10,
            };
            ISession session = Substitute.For<ISession>();
            session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(Task.FromResult(updated));
            PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: clock);
            IBeaconSyncPeer peer = manager.AddPeerForTest(session, PeerAddress, local);
            clock.Add(TimeSpan.FromSeconds(1));
            await InvokeStatusAsync(manager, peer, "RefreshStatusAsync", token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.PeerCount, Is.EqualTo(conflicting ? 0 : 1));
                Assert.That(PeerManager.ConsecutiveFailuresForTest(peer), Is.Zero);
                if (!conflicting)
                {
                    Assert.That(peer.HeadSlot, Is.EqualTo(updated.HeadSlot));
                    Assert.That(peer.GetType().GetProperty("StatusReceivedTicks")!.GetValue(peer), Is.EqualTo(clock.UtcNowOffset.UtcTicks));
                }
                else
                {
                    Assert.That(manager.GetPeerDiagnostics().Single().LastDisconnectDetail, Does.Contain("finalized checkpoint"));
                }
            }
        }
    }

    public enum HealthRecovery { Reset, ServedRequest, Decay }

    [TestCase(HealthRecovery.Reset, 0)]
    [TestCase(HealthRecovery.Reset, 7)]
    [TestCase(HealthRecovery.Reset, 8)]
    [TestCase(HealthRecovery.ServedRequest, 0)]
    [TestCase(HealthRecovery.Decay, 0)]
    public async Task Health_selection_recovers_without_forgiving_sync_failures(HealthRecovery recovery, int syncFailures)
    {
        Node node = Create();
        await using (node.P2P)
        {
            ManualTimestamper clock = new();
            PeerManager manager = recovery == HealthRecovery.Reset ? node.CreatePeerManager()
                : new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: clock);
            HeldSession held = new();
            IBeaconSyncPeer peer = manager.AddPeerForTest(recovery == HealthRecovery.Reset ? Substitute.For<ISession>() : held.Session, PeerAddress, Status);
            manager.AddPeerForTest(Substitute.For<ISession>(), UsablePeerAddress, Status);
            for (int i = 0; i < syncFailures; i++) peer.ReportFailure(PeerFailureReason.RequestFailed);
            for (int i = 0; i < 8; i++) await manager.HandleHealthFailureAsync(peer, new TimeoutException(), long.MaxValue, default);
            Assert.That(manager.GetBestPeers(0), Does.Not.Contain(peer));
            if (recovery == HealthRecovery.Reset)
                typeof(PeerManager).GetNestedType("ManagedPeer", BindingFlags.NonPublic)!.GetMethod("ResetHealthCheckFailures")!.Invoke(peer, null);
            else if (recovery == HealthRecovery.ServedRequest)
            {
                held.BlockDial.SetResult([]);
                await peer.RequestBlocksByRootAsync([Hash256.Zero], default);
            }
            else clock.Add(PeerManager.RequestFailureDecayInterval);
            Assert.That(manager.GetBestPeers(0).Contains(peer), Is.EqualTo(syncFailures < 8));
            if (recovery != HealthRecovery.Reset)
            {
                await manager.HandleHealthFailureAsync(peer, new TimeoutException(), long.MaxValue, default);
                Assert.That(manager.GetBestPeers(0), Does.Not.Contain(peer));
            }
        }
    }

    [Test]
    public async Task Health_timeout_grace_preserves_peers_but_unusable_sessions_are_dropped([Values] bool channelNeverOpened, [Values] bool notBlamed)
    {
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            IBeaconSyncPeer peer = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);
            for (int i = 0; i < 7; i++) peer.ReportFailure(PeerFailureReason.RequestFailed);
            node.Config.MinPeerCount = 2;
            ISession answering = Substitute.For<ISession>();
            answering.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(Task.FromResult(node.StatusHolder.CurrentStatus));
            IBeaconSyncPeer other = manager.AddPeerForTest(answering, PeerAddress + "Other", Status);
            await InvokeStatusAsync(manager, other, "RefreshStatusAsync", default);
            for (int i = 0; i < 8 && manager.PeerCount == 2; i++)
            {
                ReqRespTimeoutException timeout = new("timed out") { ChannelNeverOpened = channelNeverOpened, NotBlamed = notBlamed };
                await manager.HandleHealthFailureAsync(peer, timeout, 0, default);
            }

            Assert.That(manager.PeerCount, Is.EqualTo(channelNeverOpened && !notBlamed ? 1 : 2));
        }
    }

    [Test]
    public async Task A_successful_status_refresh_confirms_health_timeouts_with_an_independent_budget_and_drop_backoff()
    {
        Node node = Create();
        await using (node.P2P)
        {
            ManualTimestamper clock = new();
            node.Config.MinPeerCount = 0;
            PeerManager manager = new(node.P2P, node.Config, node.StatusHolder, LimboLogs.Instance, timestamper: clock);
            IBeaconSyncPeer failed = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);
            ISession answering = Substitute.For<ISession>();
            answering.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(default!, default).ReturnsForAnyArgs(Task.FromResult(node.StatusHolder.CurrentStatus));
            IBeaconSyncPeer other = manager.AddPeerForTest(answering, PeerAddress + "Other", Status);
            for (int i = 0; i < 8; i++) failed.ReportFailure(PeerFailureReason.RequestFailed);
            long startedAt = clock.UtcNow.Ticks;
            clock.Add(TimeSpan.FromSeconds(1));
            await InvokeStatusAsync(manager, other, "RefreshStatusAsync", default);
            for (int i = 0; i < 8; i++)
            {
                await manager.HandleHealthFailureAsync(failed, new TimeoutException(), startedAt, default);
                Assert.That(manager.PeerCount, Is.EqualTo(i == 7 ? 1 : 2));
            }

            ulong attempts = Metrics.BeaconChainDialAttempts;
            Assert.That(await manager.TryAddPeerAsync(failed.Id, default), Is.False, "the dropped address must observe backoff before another dial");
            Assert.That(Metrics.BeaconChainDialAttempts, Is.EqualTo(attempts), "a cooling endpoint must not consume a dial slot");
        }
    }

    [Test]
    public async Task A_cancelled_health_check_does_not_change_peer_selection()
    {
        Node node = Create();
        await using (node.P2P)
        {
            PeerManager manager = node.CreatePeerManager();
            IBeaconSyncPeer peer = manager.AddPeerForTest(Substitute.For<ISession>(), PeerAddress, Status);
            using CancellationTokenSource stopped = new();
            stopped.Cancel();
            for (int i = 0; i < 8; i++) await manager.HandleHealthFailureAsync(peer, new OperationCanceledException(), 0, stopped.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(manager.GetBestPeers(0), Has.Count.EqualTo(1));
                Assert.That(PeerManager.ConsecutiveFailuresForTest(peer), Is.Zero);
            }
        }
    }

    private static Task InvokeStatusAsync(PeerManager manager, IBeaconSyncPeer peer, string method, CancellationToken token) =>
        (Task)typeof(PeerManager).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [peer, token])!;

    private static async Task FailWithoutAnswerAsync(IBeaconSyncPeer peer, HeldSession session, int dial, CancellationToken token) =>
        await FailAsync(peer.RequestDataColumnSidecarsByRootAsync(Identifiers(dial), token), peer, session.ColumnDials[dial]);

    private static async Task FailAsync(Task<IReadOnlyList<DataColumnSidecar>> request, IBeaconSyncPeer peer, TaskCompletionSource<ForkedDataColumnSidecars> dial)
    {
        dial.TrySetCanceled();
        try
        {
            await request;
            Assert.Fail("the request was expected to fail");
        }
        catch (TimeoutException e)
        {
            peer.ReportFailure(PeerFailureClassifier.Classify(e), e.Message);
        }
    }

    internal static DataColumnsByRootIdentifier[] Identifiers(int root) =>
        [new DataColumnsByRootIdentifier { BlockRoot = Keccak.Compute(root.ToString()), Columns = [(ulong)root] }];

    private static BeaconP2P CreateHost(TimeSpan requestTimeout) =>
        new(new BeaconChainConfig { P2PPort = 0 }, Spec, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()), new BeaconChainStatusHolder(Spec, Timestamper.Default),
            new LocalMetadataSource(), new DataColumnSidecarPool(), new ExecutionPayloadEnvelopePool(), LimboLogs.Instance)
        { RequestTimeout = requestTimeout };

    internal static LocalPeer.Session AddWedgedSession(BeaconP2P node)
    {
        LocalPeer localPeer = node.LocalPeerForTest!;
        LocalPeer.Session session = new(localPeer);
        lock (localPeer.Sessions)
        {
            localPeer.Sessions.Add(session);
        }

        return session;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(10, token);
        }
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan window, CancellationToken token)
    {
        long startedAt = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(startedAt) > window)
            {
                return false;
            }

            await Task.Delay(10, token);
        }

        return true;
    }

    /// <summary>A session whose column and block dials stay open until the test completes them, and end like the library's when their caller gives up.</summary>
    private sealed class HeldSession
    {
        public ISession Session { get; } = Substitute.For<ISession>();
        public List<TaskCompletionSource<ForkedDataColumnSidecars>> ColumnDials { get; } = [];
        public List<DataColumnsByRootIdentifier[]> ColumnRequests { get; } = [];
        public List<RequestTiming.Exchange> Exchanges { get; } = [];
        public TaskCompletionSource<IReadOnlyList<ForkedSignedBeaconBlock>> BlockDial { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BlockDials { get; private set; }

        public HeldSession(bool opensChannels = false)
        {
            Session.DialAsync<DataColumnSidecarsByRootProtocol, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(call =>
            {
                DataColumnsByRootIdentifier[] request = call.Arg<DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>>().Request;
                TaskCompletionSource<ForkedDataColumnSidecars> dial = new(TaskCreationOptions.RunContinuationsAsynchronously);
                call.Arg<CancellationToken>().Register(() => dial.TrySetCanceled());
                lock (ColumnDials)
                {
                    ColumnDials.Add(dial);
                    ColumnRequests.Add(request);
                    if (opensChannels)
                    {
                        Exchanges.Add(RequestTiming.Open(request));
                    }
                }

                return dial.Task;
            });
            Session.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(default!, default).ReturnsForAnyArgs(_ =>
            {
                BlockDials++;
                return BlockDial.Task;
            });
        }
    }
}
