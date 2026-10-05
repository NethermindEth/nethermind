// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Logging;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

public class PeerHealthCheckRoundTests
{
    private const int MaxConcurrentChecks = 8;

    // Under the 15 s request timeout, so a status held this long still answers and a check that ran alone shows only as a missed rendezvous.
    private static readonly TimeSpan RendezvousWait = TimeSpan.FromSeconds(10);

    [Test]
    [CancelAfter(20_000)]
    public async Task Admission_capacity_is_rechecked_within_ten_seconds(CancellationToken token)
    {
        Node local = CreateNode();
        try
        {
            local.Config.TargetPeerCount = 0;
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance);
            Task waiting = manager.WaitForAdmissionCapacityAsync(token);
            Assert.That(waiting.IsCompleted, Is.False);
            local.Config.TargetPeerCount = 1;
            await waiting.WaitAsync(TimeSpan.FromSeconds(10), token);
        }
        finally
        {
            await local.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Discovery_scheduling_never_exceeds_its_bound_and_awaits_workers_on_completion_or_cancellation([Values] bool cancel, CancellationToken token)
    {
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int active = 0;
        int started = 0;
        int maximum = 0;
        Task scheduled = PeerManager.ScheduleDialsAsync(Candidates(4), 2, _ => Task.CompletedTask, async (_, dialToken) =>
        {
            int count = Interlocked.Increment(ref active);
            Interlocked.Increment(ref started);
            int seen;
            while ((seen = Volatile.Read(ref maximum)) < count && Interlocked.CompareExchange(ref maximum, count, seen) != seen)
            {
            }

            if (count == 2)
            {
                held.TrySetResult();
            }

            try
            {
                await release.Task.WaitAsync(dialToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }, static (_, _) => { }, stop.Token);

        await held.Task.WaitAsync(token);
        await Task.Delay(200, token);
        Assert.That(Volatile.Read(ref started), Is.EqualTo(2));
        if (cancel)
        {
            await stop.CancelAsync();
            Assert.CatchAsync<OperationCanceledException>(async () => await scheduled);
        }
        else
        {
            release.TrySetResult();
            await scheduled;
            Assert.That(started, Is.EqualTo(4));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(active, Is.Zero, "the schedule returned while a dial was still running");
        Assert.That(maximum, Is.EqualTo(2), "more dials ran at once than the configured limit");
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Discovery_scheduling_waits_for_admission_capacity_before_dialling(CancellationToken token)
    {
        TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource capacity = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int dialled = 0;
        Task scheduled = PeerManager.ScheduleDialsAsync(Candidates(1), 1, async dialToken =>
        {
            waiting.TrySetResult();
            await capacity.Task.WaitAsync(dialToken);
        }, (_, _) => { Interlocked.Increment(ref dialled); return Task.CompletedTask; }, static (_, _) => { }, token);
        await waiting.Task.WaitAsync(token);
        Assert.That(dialled, Is.Zero);
        capacity.TrySetResult();
        await scheduled;
        Assert.That(dialled, Is.EqualTo(1));
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task A_failing_dial_does_not_end_discovery_scheduling(CancellationToken token)
    {
        List<string> dialled = [];
        List<string> failed = [];
        await PeerManager.ScheduleDialsAsync(Candidates(2), 1, _ => Task.CompletedTask, (candidate, _) =>
        {
            lock (dialled) dialled.Add(candidate.PeerId);
            return candidate.PeerId == "peer-0" ? Task.FromException(new InvalidOperationException("malformed record")) : Task.CompletedTask;
        }, (candidate, _) => { lock (failed) failed.Add(candidate.PeerId); }, token);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(dialled, Is.EqualTo(new[] { "peer-0", "peer-1" }), "the candidate after a failing one was never dialled");
        Assert.That(failed, Is.EqualTo(new[] { "peer-0" }));
    }

    private static async IAsyncEnumerable<BeaconPeerCandidate> Candidates(int count)
    {
        for (int i = 0; i < count; i++)
        {
            yield return new BeaconPeerCandidate($"address-{i}", $"peer-{i}", [], 1, "enr:");
            await Task.Yield();
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Unanswering_peers_are_checked_together_with_a_live_peer_whose_head_is_refreshed(CancellationToken token)
    {
        const int unansweringPeers = 3;
        Node client = CreateNode();
        SetMatchingStatus(client);
        StatusMessageV2 status = client.StatusHolder.CurrentStatus;
        ulong refreshedHead = status.HeadSlot + 1;
        using ManualResetEventSlim roundStarted = new();
        using ManualResetEventSlim allAsked = new();
        int asked = 0;
        int missedRendezvous = 0;
        StatusMessageV2 HeldUntilAllAsked(StatusMessageV2 answer)
        {
            if (roundStarted.IsSet)
            {
                if (Interlocked.Increment(ref asked) == unansweringPeers + 1)
                {
                    allAsked.Set();
                }

                if (!allAsked.Wait(RendezvousWait))
                {
                    Interlocked.Increment(ref missedRendezvous);
                }
            }

            return answer;
        }

        Node[] unanswering = [.. Enumerable.Range(0, unansweringPeers).Select(_ => CreateNode(new ScriptedStatusSource(_ => HeldUntilAllAsked(status))))];
        Node live = CreateNode(new ScriptedStatusSource(_ => HeldUntilAllAsked(WithHead(status, roundStarted.IsSet ? refreshedHead : status.HeadSlot))));
        Node[] servers = [.. unanswering, live];

        try
        {
            PeerManager peerManager = await StartAndAdmitAsync(client, servers, token);
            roundStarted.Set();
            await peerManager.RunMaintenanceRoundAsync(token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(missedRendezvous, Is.Zero, "every peer's status is requested before any is answered");
                Assert.That(peerManager.GetBestPeers(0).SingleOrDefault(p => p.Id == LoopbackAddress(live.P2P))?.HeadSlot, Is.EqualTo(refreshedHead));
            }
        }
        finally
        {
            allAsked.Set();
            await DisposeAsync(client, servers);
        }
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task A_round_checks_at_most_eight_peers_at_once_and_stops_when_cancelled(CancellationToken token)
    {
        const int peers = 2 * MaxConcurrentChecks;
        Node client = CreateNode();
        SetMatchingStatus(client);
        StatusMessageV2 status = client.StatusHolder.CurrentStatus;
        using ManualResetEventSlim roundStarted = new();
        using ManualResetEventSlim release = new();
        TaskCompletionSource maxHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FailedCheckCounter failedChecks = new();
        int held = 0;
        // Counted once per peer, and only until a check fails: a status v1 retry to the same peer is the same check, and a check that
        // failed has freed its slot for another while its server still holds, so later holds no longer show concurrency.
        Node CreateHoldingNode()
        {
            int arrived = 0;
            return CreateNode(new ScriptedStatusSource(_ =>
            {
                if (roundStarted.IsSet)
                {
                    if (Interlocked.Exchange(ref arrived, 1) == 0 && failedChecks.Count == 0 && Interlocked.Increment(ref held) >= MaxConcurrentChecks)
                    {
                        maxHeld.TrySetResult();
                    }

                    // Held until the test ends, not for a fixed time, so a slow start under load cannot let earlier checks finish first.
                    release.Wait(token);
                }

                return status;
            }));
        }

        Node[] servers = [.. Enumerable.Range(0, peers).Select(_ => CreateHoldingNode())];

        try
        {
            PeerManager peerManager = await StartAndAdmitAsync(client, servers, token, new OneLoggerLogManager(failedChecks.Logger));
            using CancellationTokenSource roundCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            roundStarted.Set();
            Task round = peerManager.RunMaintenanceRoundAsync(roundCancellation.Token);
            Assert.That(await Task.WhenAny(maxHeld.Task, round), Is.SameAs(maxHeld.Task), "the round ended before it asked enough peers at once");
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            int observedMax = Volatile.Read(ref held);
            Stopwatch stopped = Stopwatch.StartNew();
            await roundCancellation.CancelAsync();
            await Task.WhenAny(round, Task.Delay(System.Threading.Timeout.Infinite, token));
            stopped.Stop();

            using (Assert.EnterMultipleScope())
            {
                Assert.That(observedMax, Is.EqualTo(MaxConcurrentChecks));
                // Under the 15 s request timeout, so only the round's token can have ended the held checks in time.
                Assert.That(stopped.Elapsed, Is.LessThan(RendezvousWait), "cancelling the round ends the checks in flight");
                Assert.That(round.IsCanceled, Is.True);
                Assert.That(peerManager.PeerCount, Is.EqualTo(peers), "a cancelled check does not count against its peer");
                Assert.That(failedChecks.Count, Is.Zero, $"the round's token, not a failed request, ended the checks; last failure: {failedChecks.Last}");
            }
        }
        finally
        {
            release.Set();
            await DisposeAsync(client, servers);
        }
    }

    internal static async Task<PeerManager> StartAndAdmitAsync(Node client, Node[] servers, CancellationToken token, ILogManager? logManager = null)
    {
        foreach (Node node in (Node[])[.. servers, client])
        {
            await node.P2P.StartAsync(token);
        }

        AdvancingTimestamper clock = new();
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, logManager ?? LoopbackTrace.Or(LimboLogs.Instance), timestamper: clock);
        foreach (Node server in servers)
        {
            Assert.That(await peerManager.TryAddPeerAsync(LoopbackAddress(server.P2P), token), Is.True);
        }

        return peerManager;
    }

    internal sealed class AdvancingTimestamper : ITimestamper
    {
        private readonly ManualTimestamper _clock = new();
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        public DateTime UtcNow => _clock.UtcNow + _elapsed.Elapsed;
        public DateTimeOffset UtcNowOffset => new(UtcNow);
        public UnixTime UnixTime => new(UtcNow);
    }

    internal static async Task DisposeAsync(Node client, Node[] servers)
    {
        foreach (Node node in (Node[])[.. servers, client])
        {
            await node.P2P.DisposeAsync();
        }
    }

    private sealed class FailedCheckCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);
        public FailedCheckCounter() => Logger = new(new TestLogRecorder(TestLogLevels.Debug, (_, text, _) => Record(text)));
        public ILogger Logger { get; }

        private string? _last;

        public string? Last => Volatile.Read(ref _last);

        private void Record(string text)
        {
            if (text.Contains("failed health check", StringComparison.Ordinal))
            {
                Volatile.Write(ref _last, text);
                Interlocked.Increment(ref _count);
            }
        }
    }

    internal static StatusMessageV2 WithHead(StatusMessageV2 status, ulong headSlot) => new()
    {
        ForkDigest = status.ForkDigest,
        FinalizedRoot = status.FinalizedRoot,
        FinalizedEpoch = status.FinalizedEpoch,
        HeadRoot = status.HeadRoot,
        HeadSlot = headSlot,
        EarliestAvailableSlot = status.EarliestAvailableSlot,
    };
}

// Held status answers and libp2p sessions block pool threads; keep the floor raised for the rest of the test process.
[SetUpFixture]
public class ThreadPoolFloor
{
    [OneTimeSetUp]
    public static void Raise()
    {
        ThreadPool.GetMinThreads(out int workers, out int completionPorts);
        ThreadPool.SetMinThreads(Math.Max(workers, 128), completionPorts);
    }
}
