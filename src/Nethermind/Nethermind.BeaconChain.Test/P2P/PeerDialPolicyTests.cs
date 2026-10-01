// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.Discovery;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Db;
using Nethermind.Logging;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

/// <summary>Which peers are dialed, when a failed address is dialed again, and which statuses end a connection.</summary>
public class PeerDialPolicyTests
{
    private const ulong SlotsPerEpoch = 32;
    private const ulong LocalEpoch = 100;

    [Test]
    [CancelAfter(60_000)]
    public async Task Caller_cancellation_does_not_penalise_an_endpoint_when_admission_ends_with_an_error(CancellationToken token)
    {
        Node local = CreateNode();
        Node remote = CreateNode();
        SetMatchingStatus(local, remote);
        ManualTimestamper clock = new();
        await using BeaconDiscovery discovery = new(local.Config, BeaconChainSpec.Mainnet, new BeaconChainStore(new MemColumnsDb<BeaconChainDbColumns>()),
            new RangeSyncTests.FixedIPResolver(IPAddress.Loopback), clock, LimboLogs.Instance);
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, discovery, clock);
            manager.PeerAdmitted += _ =>
            {
                caller.Cancel();
                throw new InvalidOperationException("caller cancelled during admission");
            };
            string address = LoopbackAddress(remote.P2P);
            for (int attempt = 0; attempt < 3 && !caller.IsCancellationRequested; attempt++)
            {
                int quality = discovery.DialHistory.Quality(address);
                try
                {
                    await manager.TryAddPeerAsync(address, caller.Token);
                }
                catch (OperationCanceledException) when (caller.IsCancellationRequested)
                {
                }

                if (caller.IsCancellationRequested)
                {
                    Assert.That(discovery.DialHistory.Quality(address), Is.EqualTo(quality), "caller cancellation was recorded as an endpoint failure");
                }
                else
                {
                    clock.Add(PeerDialHistory.MaximumBackoff);
                }
            }

            Assert.That(caller.IsCancellationRequested, Is.True, "the admission callback never ran");
        }
        finally
        {
            await local.P2P.DisposeAsync();
            await remote.P2P.DisposeAsync();
        }
    }

    /// <summary>consensus-specs v1.7.0-beta.2 networking Status: proven finalized conflicts are disconnected; unknown checkpoints are kept.</summary>
    [TestCase(LocalEpoch, 2, -1, 0, ExpectedResult = true, TestName = "Our finalized epoch with another root conflicts")]
    [TestCase(LocalEpoch, 1, -1, 0, ExpectedResult = false, TestName = "Our finalized checkpoint does not conflict")]
    [TestCase(LocalEpoch + 1, 2, -1, 0, ExpectedResult = false, TestName = "A finalized epoch past ours is unprovable")]
    [TestCase(0UL, 2, -1, 0, ExpectedResult = false, TestName = "The genesis checkpoint is unprovable")]
    [TestCase(LocalEpoch, 0, -1, 0, ExpectedResult = true, TestName = "A zero finalized root past genesis conflicts with our finalized root")]
    [TestCase(LocalEpoch - 10, 3, 0, 4, ExpectedResult = true, TestName = "An older checkpoint conflicts with our block at its start slot")]
    [TestCase(LocalEpoch - 10, 3, 0, 3, ExpectedResult = false, TestName = "An older checkpoint matching our block at its start slot does not conflict")]
    [TestCase(LocalEpoch - 10, 3, 5, 3, ExpectedResult = false, TestName = "An older checkpoint over empty slots matches our latest earlier block")]
    [TestCase(LocalEpoch - 10, 3, 5, 4, ExpectedResult = true, TestName = "An older checkpoint over empty slots conflicts with our latest earlier block")]
    [TestCase(LocalEpoch - 10, 3, -1, 0, ExpectedResult = false, TestName = "An older checkpoint our canonical index does not reach is unprovable")]
    public bool A_finalized_checkpoint_conflicts_only_when_proven_absent_from_our_chain(ulong remoteEpoch, int remoteRoot, int indexedSlotsBeforeStart, int indexedRoot)
        => Conflicts(remoteEpoch, remoteRoot, indexedSlotsBeforeStart, indexedRoot, localRoot: 1);

    [Test]
    public void No_checkpoint_conflicts_before_our_own_finalized_root_is_known() =>
        Assert.That(Conflicts(LocalEpoch, 2, 0, 3, localRoot: 0), Is.False);

    private static bool Conflicts(ulong remoteEpoch, int remoteRoot, int indexedSlotsBeforeStart, int indexedRoot, int localRoot)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        if (indexedSlotsBeforeStart >= 0)
        {
            store.ApplyCanonicalIndexChanges([(remoteEpoch * SlotsPerEpoch - (ulong)indexedSlotsBeforeStart, Root(indexedRoot))], remoteEpoch * SlotsPerEpoch);
        }

        StatusMessageV2 local = new() { FinalizedEpoch = LocalEpoch, FinalizedRoot = Root(localRoot) };
        StatusMessageV2 remote = new() { FinalizedEpoch = remoteEpoch, FinalizedRoot = Root(remoteRoot) };
        return PeerManager.ConflictsWithFinalized(remote, local, store, SlotsPerEpoch);
    }

    [Test]
    public void An_index_that_ends_before_the_checkpoint_cannot_prove_a_conflict()
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        ulong start = (LocalEpoch - 1) * SlotsPerEpoch;
        store.ApplyCanonicalIndexChanges([(start - 1, Root(3))], start - 1);
        StatusMessageV2 local = new() { FinalizedEpoch = LocalEpoch, FinalizedRoot = Root(1) };
        StatusMessageV2 remote = new() { FinalizedEpoch = LocalEpoch - 1, FinalizedRoot = Root(2) };
        Assert.That(PeerManager.ConflictsWithFinalized(remote, local, store, SlotsPerEpoch), Is.False);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_peer_with_a_conflicting_finalized_checkpoint_is_not_admitted([Values] bool conflicting, CancellationToken token)
    {
        Node local = CreateNode();
        Node remote = CreateNode();
        SetMatchingStatus(local, remote);
        local.StatusHolder.CurrentStatus.FinalizedRoot = Root(1);
        remote.StatusHolder.CurrentStatus.FinalizedRoot = Root(conflicting ? 2 : 1);
        try
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            ManualTimestamper clock = new();
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: clock);

            Assert.That(await AdmitWithRetriesAsync(manager, remote, clock, token, attempts: 3), Is.EqualTo(!conflicting));
            if (conflicting)
            {
                // A lost dial looks the same from outside, so the recorded disconnect proves the refusal was for the checkpoint.
                PeerManager.PeerDiagnostics diagnostics = manager.GetPeerDiagnostics().Single(d => d.PeerId == remote.P2P.LocalPeerId!.ToString());
                using (Assert.EnterMultipleScope())
                {
                    Assert.That(diagnostics.LastDisconnectReason, Is.EqualTo("IrrelevantNetwork"));
                    Assert.That(diagnostics.LastDisconnectDetail, Does.Contain("finalized checkpoint"));
                }

                await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session of a peer on a disjoint finalized chain was left open");
            }
        }
        finally
        {
            await local.P2P.DisposeAsync();
            await remote.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_dropped_discovered_peer_is_dialed_again_once_its_backoff_ends(CancellationToken token)
    {
        Node local = CreateNode();
        Node remote = CreateNode();
        SetMatchingStatus(local, remote);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Channel<BeaconPeerCandidate> offered = Channel.CreateUnbounded<BeaconPeerCandidate>();
        Task dialing = Task.CompletedTask;
        try
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            ManualTimestamper clock = new();
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: clock);
            dialing = manager.DialDiscoveredPeersAsync(offered.Reader.ReadAllAsync(stop.Token), stop.Token);
            string address = LoopbackAddress(remote.P2P);
            BeaconPeerCandidate candidate = new(address, remote.P2P.LocalPeerId!.ToString(), [], 1,
                new BeaconNodeRecordProvider(TestItem.PrivateKeyA, IPAddress.Loopback, 9000, 9000, EnrForkId.Compute(BeaconChainSpec.Mainnet, 0), custodyGroupCount: 4).Current.ToString());

            Assert.That(await OfferUntilConnectedAsync(), Is.True, "the discovered peer was never admitted");

            (int target, int max) = (local.Config.TargetPeerCount, local.Config.MaxPeerCount);
            (local.Config.TargetPeerCount, local.Config.MaxPeerCount) = (0, 0);
            await manager.RunMaintenanceRoundAsync(token);
            Assert.That(manager.PeerCount, Is.Zero, "the trim did not drop the peer");
            (local.Config.TargetPeerCount, local.Config.MaxPeerCount) = (target, max);

            await offered.Writer.WriteAsync(candidate, token);
            await Task.Delay(TimeSpan.FromSeconds(1), token);
            Assert.That(manager.PeerCount, Is.Zero, "a dropped address was dialed again before its backoff ended");

            clock.Add(PeerDialHistory.MaximumBackoff);
            Assert.That(await OfferUntilConnectedAsync(), Is.True, "a dropped discovered peer was never dialed again");

            async Task<bool> OfferUntilConnectedAsync()
            {
                // The pinned libp2p can lose a fresh session, so a failed attempt waits out its backoff and is offered again.
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    await offered.Writer.WriteAsync(candidate, token);
                    if (await EventuallyAsync(() => manager.PeerCount == 1, TimeSpan.FromSeconds(10), token))
                    {
                        return true;
                    }

                    clock.Add(PeerDialHistory.MaximumBackoff);
                }

                return false;
            }
        }
        finally
        {
            await stop.CancelAsync();
            try
            {
                await dialing;
            }
            catch (OperationCanceledException)
            {
            }

            await local.P2P.DisposeAsync();
            await remote.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_static_peer_is_redialed_without_waiting_out_a_backoff(CancellationToken token)
    {
        Node local = CreateNode();
        Node remote = CreateNode();
        SetMatchingStatus(local, remote);
        remote.StatusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = [0xde, 0xad, 0xbe, 0xef],
            FinalizedRoot = Hash256.Zero,
            HeadRoot = Hash256.Zero,
        };
        try
        {
            await remote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            local.Config.StaticPeers = LoopbackAddress(remote.P2P);
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: new ManualTimestamper());

            await manager.RunMaintenanceRoundAsync(token);
            Assert.That(manager.PeerCount, Is.Zero, "a static peer on another fork was admitted");

            SetMatchingStatus(remote);
            // The clock never moves, so only a dial that ignores the backoff of the failed one can connect.
            for (int round = 0; round < 3 && manager.PeerCount == 0; round++)
            {
                await manager.RunMaintenanceRoundAsync(token);
            }

            Assert.That(manager.PeerCount, Is.EqualTo(1), "a configured static peer waited out a discovery backoff");
        }
        finally
        {
            await local.P2P.DisposeAsync();
            await remote.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_dial_waiting_for_a_dial_slot_keeps_its_whole_connection_timeout(CancellationToken token)
    {
        Node local = CreateNode();
        SetMatchingStatus(local);
        using BlockingStatusSource hanging = new(local.StatusHolder);
        // Longer than the time the dial below waits past the hanging one's start, shorter than a whole dial timeout.
        DelayedStatusSource slow = new(local.StatusHolder, TimeSpan.FromSeconds(2));
        Node hangingRemote = CreateNode(hanging);
        Node slowRemote = CreateNode(slow);
        local.Config.MaxConcurrentOutboundDials = 1;
        try
        {
            await hangingRemote.P2P.StartAsync(token);
            await slowRemote.P2P.StartAsync(token);
            await local.P2P.StartAsync(token);
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: new ManualTimestamper());

            Task<bool> holdsSlot = manager.TryAddPeerAsync(LoopbackAddress(hangingRemote.P2P), token);
            await hanging.Entered.WaitAsync(token);
            Task<bool> waitsForSlot = manager.TryAddPeerAsync(LoopbackAddress(slowRemote.P2P), token);

            Assert.That(await holdsSlot, Is.False, "the dial to a peer that never answers status did not time out");
            Assert.That(await waitsForSlot, Is.True, "the time a dial waited for a slot was taken from its connection timeout");
        }
        finally
        {
            hanging.Release();
            await local.P2P.DisposeAsync();
            await hangingRemote.P2P.DisposeAsync();
            await slowRemote.P2P.DisposeAsync();
        }
    }

    private static Hash256 Root(int fill) => fill == 0 ? Hash256.Zero : new Hash256(Enumerable.Repeat((byte)fill, Hash256.Size).ToArray());

    private static async Task<bool> AdmitWithRetriesAsync(PeerManager manager, Node remote, ManualTimestamper clock, CancellationToken token, int attempts)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            if (await manager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token))
            {
                return true;
            }

            clock.Add(PeerDialHistory.MaximumBackoff);
        }

        return false;
    }

    private static async Task<bool> EventuallyAsync(Func<bool> condition, TimeSpan bound, CancellationToken token)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(bound);
        while (!condition())
        {
            if (bounded.IsCancellationRequested)
            {
                return false;
            }

            await Task.Delay(50, token);
        }

        return true;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken token, string failure)
    {
        if (!await EventuallyAsync(condition, TimeSpan.FromSeconds(20), token))
        {
            Assert.Fail(failure);
        }
    }

    /// <summary>Blocks every status read until <see cref="Release"/>, and signals the first one.</summary>
    private sealed class BlockingStatusSource(IBeaconChainStatusSource inner) : IBeaconChainStatusSource, IDisposable
    {
        private readonly ManualResetEventSlim _released = new(false);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.Set();

        public StatusMessageV2 CurrentStatus
        {
            get
            {
                _entered.TrySetResult();
                _released.Wait();
                return inner.CurrentStatus;
            }
        }

        public Hash256 JustifiedRoot => inner.JustifiedRoot;

        public bool ExecutionInSync => inner.ExecutionInSync;

        public void Dispose() => _released.Dispose();
    }

    /// <summary>Answers every status read after <paramref name="delay"/>.</summary>
    private sealed class DelayedStatusSource(IBeaconChainStatusSource inner, TimeSpan delay) : IBeaconChainStatusSource
    {
        public StatusMessageV2 CurrentStatus
        {
            get
            {
                Thread.Sleep(delay);
                return inner.CurrentStatus;
            }
        }

        public Hash256 JustifiedRoot => inner.JustifiedRoot;

        public bool ExecutionInSync => inner.ExecutionInSync;
    }
}
