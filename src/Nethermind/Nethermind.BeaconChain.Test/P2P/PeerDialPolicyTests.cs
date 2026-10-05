// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
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
using static Nethermind.BeaconChain.Test.P2P.PeerBandTests;

namespace Nethermind.BeaconChain.Test.P2P;

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
        await using PeerHostScope hosts = new(remote.P2P, local.P2P);
        await remote.P2P.StartAsync(token);
        await local.P2P.StartAsync(token);
        PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, discovery, clock);
        manager.PeerAdmitted += _ =>
        {
            caller.Cancel();
            throw new InvalidOperationException("caller cancelled during admission");
        };
        string address = LoopbackAddress(remote.P2P);
        int quality = discovery.DialHistory.Quality(address);
        try
        {
            await manager.TryAddPeerAsync(address, caller.Token);
        }
        catch (OperationCanceledException) when (caller.IsCancellationRequested)
        {
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(caller.IsCancellationRequested, Is.True, "the admission callback never ran");
            Assert.That(discovery.DialHistory.Quality(address), Is.EqualTo(quality), "caller cancellation was recorded as an endpoint failure");
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
    [TestCase(LocalEpoch, 2, 0, 3, 0, ExpectedResult = false, TestName = "No_checkpoint_conflicts_before_our_own_finalized_root_is_known")]
    [TestCase(LocalEpoch - 1, 2, 1, 3, 1, 1, ExpectedResult = false, TestName = "An_index_that_ends_before_the_checkpoint_cannot_prove_a_conflict")]
    public bool A_finalized_checkpoint_conflicts_only_when_proven_absent_from_our_chain(ulong remoteEpoch, int remoteRoot, int indexedSlotsBeforeStart, int indexedRoot, int localRoot = 1, int indexedEndSlotsBeforeStart = 0)
    {
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        if (indexedSlotsBeforeStart >= 0)
        {
            store.ApplyCanonicalIndexChanges([(remoteEpoch * SlotsPerEpoch - (ulong)indexedSlotsBeforeStart, Root(indexedRoot))], remoteEpoch * SlotsPerEpoch - (ulong)indexedEndSlotsBeforeStart);
        }

        StatusMessageV2 local = new() { FinalizedEpoch = LocalEpoch, FinalizedRoot = Root(localRoot) };
        StatusMessageV2 remote = new() { FinalizedEpoch = remoteEpoch, FinalizedRoot = Root(remoteRoot) };
        return PeerManager.ConflictsWithFinalized(remote, local, store, SlotsPerEpoch);
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
        await using PeerHostScope hosts = new(remote.P2P, local.P2P);
        await remote.P2P.StartAsync(token);
        await local.P2P.StartAsync(token);
        ManualTimestamper clock = new();
        PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: clock);

        Assert.That(await manager.TryAddPeerAsync(LoopbackAddress(remote.P2P), token), Is.EqualTo(!conflicting));
        if (conflicting)
        {
            // The recorded disconnect proves the refusal was for the checkpoint, not a failed dial.
            PeerManager.PeerDiagnostics diagnostics = manager.GetPeerDiagnostics().Single(d => d.PeerId == remote.P2P.LocalPeerId!.ToString());
            using (Assert.EnterMultipleScope())
            {
                Assert.That(diagnostics.LastDisconnectReason, Is.EqualTo("IrrelevantNetwork"));
                Assert.That(diagnostics.LastDisconnectDetail, Does.Contain("finalized checkpoint"));
            }

            await WaitUntilAsync(() => local.P2P.SessionCountForTest == 0, token, "the session of a peer on a disjoint finalized chain was left open");
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
                await offered.Writer.WriteAsync(candidate, token);
                return await EventuallyAsync(() => manager.PeerCount == 1, TimeSpan.FromSeconds(10), token);
            }
        }
        finally
        {
            await stop.CancelAsync();
            await AwaitStoppedOperationAndDisposeNodeAsync(dialing, local);
            await remote.P2P.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_dial_to_an_address_that_drops_every_connection_is_made_once_then_backs_off(CancellationToken token)
    {
        Node local = CreateNode();
        using TcpListener dead = new(IPAddress.Loopback, 0);
        dead.Start();
        int connections = 0;
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task accepting = Task.Run(async () =>
        {
            while (true)
            {
                using Socket socket = await dead.AcceptSocketAsync(stop.Token);
                Interlocked.Increment(ref connections);
            }
        }, stop.Token);
        try
        {
            await local.P2P.StartAsync(token);
            PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: new ManualTimestamper());
            // Only the side with the lower peer id redials after a crossing dial, so this node would redial this peer.
            Nethermind.Libp2p.Core.PeerId remote;
            do
            {
                remote = new Nethermind.Libp2p.Core.Identity(privateKey: null, Nethermind.Libp2p.Core.Dto.KeyType.Secp256K1).PeerId;
            }
            while (string.CompareOrdinal(local.P2P.LocalPeerId!.ToString(), remote.ToString()) >= 0);

            string address = $"/ip4/127.0.0.1/tcp/{((IPEndPoint)dead.LocalEndpoint).Port}/p2p/{remote}";
            bool first = await manager.TryAddPeerAsync(address, token);
            bool second = await manager.TryAddPeerAsync(address, token);
            await Task.Delay(TimeSpan.FromSeconds(1), token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(first, Is.False);
                Assert.That(second, Is.False, "the failed address is dialed again before its backoff ends");
                Assert.That(Volatile.Read(ref connections), Is.EqualTo(1), "the failed dial was repeated");
            }
        }
        finally
        {
            await stop.CancelAsync();
            await AwaitStoppedOperationAndDisposeNodeAsync(accepting, local);
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_static_peer_is_redialed_without_waiting_out_a_backoff(CancellationToken token)
    {
        Node local = CreateNode();
        SetMatchingStatus(local);
        bool compatible = false;
        StatusMessageV2 incompatible = new()
        {
            ForkDigest = [0xde, 0xad, 0xbe, 0xef],
            FinalizedRoot = Hash256.Zero,
            HeadRoot = Hash256.Zero,
        };
        Node remote = CreateNode(new ScriptedStatusSource(_ => Volatile.Read(ref compatible) ? local.StatusHolder.CurrentStatus : incompatible));
        await using PeerHostScope hosts = new(remote.P2P, local.P2P);
        await remote.P2P.StartAsync(token);
        await local.P2P.StartAsync(token);
        local.Config.StaticPeers = LoopbackAddress(remote.P2P);
        PeerManager manager = new(local.P2P, local.Config, local.StatusHolder, LimboLogs.Instance, timestamper: new ManualTimestamper());

        await manager.RunMaintenanceRoundAsync(token);
        Assert.That(manager.PeerCount, Is.Zero, "a static peer on another fork was admitted");
        await WaitUntilAsync(() => remote.P2P.SessionCountForTest == 0 && local.P2P.SessionCountForTest == 0, token, "the session of the peer on another fork was left open");

        Volatile.Write(ref compatible, true);
        // The clock never moves, so only a dial that ignores the backoff of the failed one can connect.
        for (int round = 0; round < 3 && manager.PeerCount == 0; round++)
        {
            await manager.RunMaintenanceRoundAsync(token);
        }

        Assert.That(manager.PeerCount, Is.EqualTo(1), "a configured static peer waited out a discovery backoff");
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

    private static async Task AwaitStoppedOperationAndDisposeNodeAsync(Task operation, Node node)
    {
        try
        {
            await operation;
        }
        catch (OperationCanceledException)
        {
        }

        await node.P2P.DisposeAsync();
    }
}
