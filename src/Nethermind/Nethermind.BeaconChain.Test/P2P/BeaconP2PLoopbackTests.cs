// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Autofac;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Test.P2P.ReqResp;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
using NSubstitute;
using NSubstitute.Core;

namespace Nethermind.BeaconChain.Test.P2P;

public class BeaconP2PLoopbackTests
{
    // A Fulu/BPO2-era mainnet slot so the fork digest exercises the EIP-7892 blob-parameter masking.
    private const ulong AnchorSlot = 13_410_304;

    private static readonly BeaconChainSpec Spec = BeaconChainSpec.Mainnet;

    [Test]
    [CancelAfter(60_000)]
    public async Task A_cancelled_dial_does_not_lose_the_peer([Values] bool alreadyConnected, CancellationToken token)
    {
        await using BeaconP2P server = PeerSessionNodes.Create().P2P;
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await server.StartAsync(token);
        await client.StartAsync(token);
        Multiaddress address = PeerSessionNodes.LoopbackAddress(server);
        ISession? established = alreadyConnected ? await PeerSessionNodes.DialAsync(client, server, token) : null;
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Assert.That(async () => await client.DialPeerAsync(address, cancelled.Token), Throws.InstanceOf<OperationCanceledException>(), "fixture: the dial is cancelled");
        ISession resumed = await client.DialPeerAsync(address, token).WaitAsync(TimeSpan.FromSeconds(20), token);
        Assert.That(resumed, Is.Not.Null);
        if (alreadyConnected)
        {
            Assert.That(resumed, Is.SameAs(established), "a cancelled dial must leave the established session usable");
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Disposal_closes_inflight_connections_and_rejects_session_admission_after_its_snapshot(
        [Values] bool sharedHost, CancellationToken token)
    {
        await using ServiceProvider? services = sharedHost ? new ServiceCollection().AddLibp2p().BuildServiceProvider() : null;
        await using BeaconP2P? beacon = sharedHost ? null : PeerSessionNodes.Create().P2P;
        if (beacon is not null) await beacon.StartAsync(token);
        LocalPeer peer = beacon?.LocalPeerForTest! ?? (LocalPeer)services!.GetRequiredService<IPeerFactory>().Create();
        IProtocolStackSettings settings = (IProtocolStackSettings)typeof(LocalPeer)
            .GetField("_protocolStackSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(peer)!;
        ProtocolRef protocol = settings.TopProtocols![0];
        LocalPeer.Session opening = new(peer);
        opening.State.RemoteAddress = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{new Identity().PeerId}");
        peer.CreateConnection(protocol, opening, isListener: false, activity: null);
        Assert.That(peer.Sessions, Is.Empty, "fixture: the connection has not reached session admission");
        TaskCompletionSource cancelling = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        using CancellationTokenRegistration hold = opening.Closed.Register(() =>
        {
            cancelling.TrySetResult();
            release.Wait(token);
        });
        Task disposing = Task.Run(async () => await peer.DisposeAsync(), token);
        try
        {
            await cancelling.Task.WaitAsync(token);
            LocalPeer.Session late = new(peer);
            late.State.RemoteAddress = opening.State.RemoteAddress;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(disposing.IsCompleted, Is.False, "fixture: disposal has snapshotted connections and is still draining");
                Assert.That(opening.Closed.IsCancellationRequested, Is.True, "disposal also closes pre-admission connections");
                Assert.That(() => peer.UpgradeToSession(opening, protocol, isListener: false, activity: null), Throws.TypeOf<ChannelClosedException>());
                Assert.That(() => peer.CreateConnection(protocol, late, isListener: false, activity: null), Throws.TypeOf<ChannelClosedException>());
                Assert.That(late.Closed.IsCancellationRequested, Is.True);
                Assert.That(peer.Sessions, Is.Empty, "no late session or identify callback is admitted");
            }
        }
        finally
        {
            release.Set();
            await disposing.WaitAsync(token);
            await peer.DisposeAsync();
        }
    }

    [Test]
    [CancelAfter(90_000)]
    public async Task A_host_disposed_during_a_dial_leaves_no_session_open([Values] bool callerStopsWaiting, CancellationToken token)
    {
        UnansweredIdentifyProtocol? identify = null;
        await using PlainPeer server = await PlainPeer.StartAsync(settings => identify = new UnansweredIdentifyProtocol(false, settings), token);
        BeaconP2P client = PeerSessionNodes.Create().P2P;
        LocalPeer? clientPeer = null;
        Task<ISession>? dial = null, remaining = null;
        using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            await client.StartAsync(token);
            clientPeer = client.LocalPeerForTest!;
            dial = client.DialPeerAsync(server.Address, caller.Token);
            await identify!.Requested.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
            remaining = dial;
            if (callerStopsWaiting)
            {
                remaining = clientPeer.DialAsync(server.Address, token);
                await caller.CancelAsync();
            }
            using (Assert.EnterMultipleScope())
            {
                Assert.That(remaining.IsCompleted, Is.False, "fixture: identify keeps the dial in flight until disposal");
                Assert.That(SessionCount(clientPeer), Is.GreaterThan(0), "fixture: the dialer created its session before disposal");
                Assert.That(SessionCount(server.Peer), Is.GreaterThan(0), "fixture: the remote created its session before disposal");
            }
        }
        finally
        {
            try { await client.DisposeAsync(); }
            finally
            {
                foreach (Task<ISession>? waiter in new[] { dial, remaining })
                {
                    if (waiter is not null) await ((Task)waiter).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                }
            }
        }
        token.ThrowIfCancellationRequested();

        await PeerSessionNodes.WaitUntilAsync(() => SessionCount(clientPeer!) == 0 && SessionCount(server.Peer) == 0,
            "disposing the dialer left a local or remote session open", token, TimeSpan.FromSeconds(45), pollDelayMilliseconds: 50);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SessionCount(clientPeer!), Is.Zero, "the disposed dialer left a session open");
            Assert.That(SessionCount(server.Peer), Is.Zero, "the remote retained the session after dialer disposal");
            Assert.That(client.IdentifyTimeoutsForTest, Is.Zero, "disposal closes the in-flight dial before the identify timeout");
        }

        static int SessionCount(LocalPeer peer)
        {
            lock (peer.Sessions) return peer.Sessions.Count;
        }
    }

    [Test]
    [NonParallelizable]
    [CancelAfter(60_000)]
    public async Task An_abandoned_dial_that_fails_is_observed(CancellationToken token)
    {
        await using BeaconP2P client = PeerSessionNodes.Create().P2P;
        await client.StartAsync(token);
        using TcpListener closed = new(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        string refusing = $"/ip4/127.0.0.1/tcp/{port}/p2p/{new Identity().PeerId}";
        int unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs args)
        {
            if (args.Exception.Flatten().InnerExceptions.Any(e => e.Message.Contains(refusing)))
            {
                Interlocked.Increment(ref unobserved);
            }
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await AbandonAsync(client, Multiaddress.Decode(refusing), token);
            await Task.Delay(1000, token);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        Assert.That(Volatile.Read(ref unobserved), Is.Zero, "the failed dial reached UnobservedTaskException");
    }

    // Kept out of the test method so no local holds the dial task when the collector runs.
    private static async Task AbandonAsync(BeaconP2P client, Multiaddress address, CancellationToken token)
    {
        using CancellationTokenSource abandoned = CancellationTokenSource.CreateLinkedTokenSource(token);
        await abandoned.CancelAsync();
        Assert.That(async () => await client.DialPeerAsync(address, abandoned.Token), Throws.InstanceOf<OperationCanceledException>(), "fixture: the caller stops waiting");
    }

    /// <summary>Disposal releases the TCP listener without requiring caller cancellation.</summary>
    [Test]
    [CancelAfter(30_000)]
    public async Task A_disposed_host_no_longer_accepts_TCP_connections(CancellationToken token)
    {
        BeaconP2P host = PeerSessionNodes.Create().P2P;
        await host.StartAsync(token);
        int port = host.ListenAddresses.Single().ToEndPoint().Port;
        using (TcpClient before = new())
        {
            await before.ConnectAsync(IPAddress.Loopback, port, token);
        }

        await host.DisposeAsync();

        using TcpClient after = new();
        Assert.That(async () => await after.ConnectAsync(IPAddress.Loopback, port, token), Throws.InstanceOf<SocketException>());
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Start_rejects_an_occupied_TCP_port(CancellationToken token)
    {
        using TcpListener occupied = new(IPAddress.Any, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        PeerSessionNodes.Node node = PeerSessionNodes.Create();
        node.Config.P2PPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using BeaconP2P host = node.P2P;
        try
        {
            Assert.That(async () => await host.StartAsync(lifetime.Token),
                Throws.TypeOf<InvalidOperationException>().With.Message.Contains($"failed to bind TCP port {node.Config.P2PPort}"));
            Assert.That(() => host.GetTopic("test"), Throws.TypeOf<InvalidOperationException>(), "pubsub must not start after a failed bind");
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    [Test]
    [CancelAfter(30_000)]
    public async Task Established_session_lookup_waits_for_concurrent_removal(CancellationToken token)
    {
        using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        await using BeaconP2P host = PeerSessionNodes.Create().P2P;
        try
        {
            await host.StartAsync(lifetime.Token);
            LocalPeer peer = host.LocalPeerForTest!;
            PeerId peerId = peer.Identity.PeerId;
            LocalPeer.Session candidate = new(peer);
            candidate.State.RemotePublicKey = peer.Identity.PublicKey;
            candidate.State.RemoteAddress = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{peerId}");
            TaskCompletionSource<ISession?> lookup = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Thread reader = new(() =>
            {
                try
                {
                    host.TryGetEstablishedSession(peerId, out ISession? session);
                    lookup.SetResult(session);
                }
                catch (Exception error)
                {
                    lookup.SetException(error);
                }
            })
            { IsBackground = true };
            try
            {
                lock (peer.Sessions)
                {
                    peer.Sessions.Add(candidate);
                    reader.Start();
                    Assert.That(SpinWait.SpinUntil(() => lookup.Task.IsCompleted || (reader.ThreadState & ThreadState.WaitSleepJoin) != 0,
                        TimeSpan.FromSeconds(5)), Is.True, "the lookup thread did not run");
                    Assert.That(lookup.Task.IsCompleted, Is.False, "lookup must wait for the library's session lock");
                    peer.Sessions.Remove(candidate);
                }

                Assert.That(await lookup.Task.WaitAsync(token), Is.Null, "the removed session must not appear in the snapshot");
            }
            finally
            {
                await lookup.Task.WaitAsync(token);
            }
        }
        finally
        {
            await lifetime.CancelAsync();
        }
    }

    [Test]
    public async Task A_failed_p2p_listener_bind_can_be_started_again()
    {
        using TcpListener occupied = new(IPAddress.Any, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        await using IContainer container = BeaconChainTestContainer.Builder(config: new BeaconChainConfig
        {
            P2PPort = ((IPEndPoint)occupied.LocalEndpoint).Port,
        }).Build();
        BeaconP2P p2p = container.Resolve<BeaconP2P>();
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(20));
        await Assert.CatchAsync<Exception>(() => p2p.StartAsync(timeout.Token));
        Assert.That(p2p.ListenAddresses, Is.Empty);
        Assert.That(p2p.LocalPeerForTest, Is.Null, "a failed bind disposes the peer before retrying");
        occupied.Stop();
        await p2p.StartAsync(timeout.Token);

        Assert.That(p2p.ListenAddresses, Is.Not.Empty);
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task A_second_identity_on_the_same_host_draws_on_the_block_budget_the_first_spent(CancellationToken token)
    {
        Node server = CreateNode();
        Node[] clients = [.. Enumerable.Range(0, ReqRespProtocolBase.PeersPerHost + 1).Select(_ => CreateNode())];
        BeaconP2P[] nodes = [server.P2P, .. clients.Select(client => client.P2P)];
        await using PeerHostScope hosts = new(nodes);
        await hosts.StartAsync(token, nodes);
        List<ISession> sessions = [];
        foreach (Node client in clients) sessions.Add(await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token));

        // The empty store answers ResourceUnavailable, but only after the request is charged.
        List<byte> codes = [];
        for (int i = 0; i < clients.Length; i++)
        {
            codes.Add((await Assert.CatchAsync<Eth2ReqRespException>(() => clients[i].P2P.RequestBlocksByRangeAsync(sessions[i], AnchorSlot, 128, token)))!.ResponseCode);
        }

        using IDisposable scope = Assert.EnterMultipleScope();
        Assert.That(codes[..^1], Is.All.EqualTo(ReqRespFraming.ResponseCode.ResourceUnavailable), "honest nodes behind one address are served after the first spent a peer's budget");
        Assert.That(codes[^1], Is.EqualTo(ReqRespFraming.ResponseCode.RateLimited), "every key connects from 127.0.0.1, so one more has no budget of its own");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Metadata_advertisement_and_wire_match_the_host_role([Values] bool lightClient, CancellationToken token)
    {
        const string metadataV2 = "/eth2/beacon_chain/req/metadata/2/ssz_snappy";
        const string metadataV3 = "/eth2/beacon_chain/req/metadata/3/ssz_snappy";
        Node server = CreateNode(lightClient: lightClient);
        MetaDataV3 local = server.MetadataSource.Current;
        local.SeqNumber = 0x0807060504030201;
        local.Attnets![0] = true;
        local.Attnets[8] = true;
        local.Attnets[63] = true;
        local.Syncnets![3] = true;
        local.CustodyGroupCount = lightClient ? 0ul : 128ul;
        Node client = CreateNode(lightClient: true);
        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);
        ISession session = await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token);
        BeaconP2P.SessionInfo info = await client.P2P.GetSessionInfoAsync(session, token);
        Assert.That(info.Identify, Is.Not.Null, "the advertised protocols came from the remote identify response");

        if (lightClient)
            Assert.That(async () => await session.DialAsync<MetaDataProtocolV3, Uint64Request, MetaDataV3>(new(0), token),
                Throws.TypeOf<ChannelClosedException>(), "an optimistic metadata v3 proposal must be refused even without consulting identify");
        else
            Assert.That(async () => await session.DialAsync<MetaDataProtocolV2, Uint64Request, MetaDataV2>(new(0), token),
                Throws.TypeOf<ChannelClosedException>(), "full-node metadata negotiation is unchanged");

        string served = lightClient ? metadataV2 : metadataV3;
        (byte[] response, _) = await TrailingRequestBytesLoopbackTests.RequestAsync(server.P2P, served, [], halfClose: true, token);
        using MemoryStream stream = new(response);
        ResponseChunk? chunk = await ReqRespFraming.ReadResponseChunkAsync(stream, contextBytesLength: 0, maxSize: 25, token);
        byte[] expected = [1, 2, 3, 4, 5, 6, 7, 8, 1, 1, 0, 0, 0, 0, 0, 128, 8];
        if (!lightClient) expected = [.. expected, 128, 0, 0, 0, 0, 0, 0, 0];
        byte[] decoded = lightClient
            ? MetaDataV2.Encode(await session.DialAsync<MetaDataProtocolV2, Uint64Request, MetaDataV2>(new(0), token))
            : MetaDataV3.Encode(await client.P2P.RequestMetaDataAsync(session, token));

        using IDisposable assertions = Assert.EnterMultipleScope();
        Assert.That(info.Identify!.Protocols, Does.Contain(served));
        Assert.That(info.Identify.Protocols, Does.Not.Contain(lightClient ? metadataV3 : metadataV2));
        Assert.That(chunk?.Result, Is.EqualTo(ReqRespFraming.ResponseCode.Success));
        Assert.That(chunk?.Payload, Is.EqualTo(expected), "Altair metadata is exactly 17 bytes and never claims custody; full nodes retain Fulu's 25-byte metadata");
        Assert.That(decoded, Is.EqualTo(expected), "light clients can still query full-node metadata v3, and v2 responses decode canonically");
        Assert.That(stream.Position, Is.EqualTo(stream.Length), "one success chunk and no trailing data");
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Two_hosts_exchange_status_blocks_ping_metadata_and_goodbye(CancellationToken token)
    {
        (SignedBeaconBlock anchor, Hash256 anchorRoot, SignedBeaconBlock[] chain) =
            TestChain.BuildLinkedChain(AnchorSlot, AnchorSlot + 1, AnchorSlot + 2, AnchorSlot + 4);
        Hash256[] chainRoots = [.. chain.Select(b => SszRoots.HashTreeRoot(b.Message!))];

        byte[] forkDigest = ForkDigest.Compute(Spec, Spec.GetEpoch(Spec.GetSlotAtTime((ulong)Timestamper.Default.UnixTime.Seconds)));
        StatusMessageV2 serverStatus = new()
        {
            ForkDigest = forkDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
            HeadRoot = chainRoots[^1],
            HeadSlot = AnchorSlot + 4,
            EarliestAvailableSlot = AnchorSlot,
        };

        Node server = CreateNode();
        TestChain.Persist(server.Store, anchor, anchorRoot, chain);
        server.StatusHolder.CurrentStatus = serverStatus;
        server.MetadataSource.Current.SeqNumber = 42;
        server.MetadataSource.Current.CustodyGroupCount = 128;

        Node client = CreateNode();
        client.StatusHolder.CurrentStatus = new StatusMessageV2
        {
            ForkDigest = forkDigest,
            FinalizedRoot = anchorRoot,
            FinalizedEpoch = Spec.GetEpoch(AnchorSlot),
            HeadRoot = anchorRoot,
            HeadSlot = AnchorSlot,
            EarliestAvailableSlot = AnchorSlot,
        };

        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);

        ISession toServer = await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token);
        ISession toClient = await PeerSessionNodes.DialAsync(server.P2P, client.P2P, token);

        StatusMessageV2 statusSeenByClient = await client.P2P.RequestStatusAsync(toServer, token);
        StatusMessageV2 statusSeenByServer = await server.P2P.RequestStatusAsync(toClient, token);
        AssertStatus(statusSeenByClient, serverStatus);
        AssertStatus(statusSeenByServer, client.StatusHolder.CurrentStatus);

        IReadOnlyList<ForkedSignedBeaconBlock> blocks = await client.P2P.RequestBlocksByRangeAsync(toServer, AnchorSlot + 1, 8, token);
        Assert.That(blocks.Select(b => b.ComputeMessageRoot()), Is.EqualTo(chainRoots), "blocks by range roots");

        IReadOnlyList<ForkedSignedBeaconBlock> byRoot = await client.P2P.RequestBlocksByRootAsync(toServer, [chainRoots[1]], token);
        Assert.That(byRoot.Select(b => b.ComputeMessageRoot()), Is.EqualTo(new[] { chainRoots[1] }), "blocks by root");

        Assert.That(await client.P2P.PingAsync(toServer, token), Is.EqualTo(42ul), "ping returns the server metadata seq");

        MetaDataV3 metadata = await client.P2P.RequestMetaDataAsync(toServer, token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(metadata.SeqNumber, Is.EqualTo(42ul), "metadata seq");
            Assert.That(metadata.CustodyGroupCount, Is.EqualTo(128ul), "metadata custody group count");
            Assert.That(metadata.Attnets, Is.EqualTo(new BitArray(64)), "metadata attnets");
        }

        client.Config.StaticPeers = PeerSessionNodes.LoopbackAddress(server.P2P).ToString();
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        await peerManager.RunMaintenanceRoundAsync(token);
        IBeaconSyncPeer syncPeer = peerManager.GetBestPeers(AnchorSlot + 4).Single();
        Assert.That(syncPeer.HeadSlot, Is.EqualTo(AnchorSlot + 4), "peer manager records the peer head");

        await client.P2P.GoodbyeAsync(toServer, GoodbyeReason.ClientShutdown, token);
    }

    [Test]
    [CancelAfter(120_000)]
    public async Task Column_dials_resolve_to_the_requested_shape_on_the_shared_protocols(CancellationToken token)
    {
        BeaconChainSpec spec = BeaconChainSpec.Sepolia;
        ulong gloasSlot = spec.GloasForkEpoch * spec.SlotsPerEpoch + 5;
        Hash256 heldRoot = Keccak.Compute("held Gloas block");
        Hash256 pendingRoot = Keccak.Compute("pending Gloas block");
        StatusMessageV2 status = new()
        {
            ForkDigest = ForkDigest.Compute(spec, spec.GetEpoch(gloasSlot)),
            FinalizedRoot = Hash256.Zero,
            HeadRoot = heldRoot,
            HeadSlot = gloasSlot,
        };

        Node server = CreateNode(spec);
        server.StatusHolder.CurrentStatus = status;
        server.Pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, gloasSlot, heldRoot));
        server.Pool.AddGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(7, gloasSlot, heldRoot));
        server.Pool.AddPendingGloas(DataColumnSidecarGloasTestFixture.BuildSidecar(3, gloasSlot, pendingRoot), gloasSlot);
        server.Store.SetCanonicalRoot(gloasSlot, heldRoot);
        DataColumnSidecar fulu = DataColumnSidecarTestFixture.BuildValidSidecar(3, spec.GloasForkEpoch * spec.SlotsPerEpoch - 1, blobCount: 1);
        Hash256 fuluRoot = SszRoots.HashTreeRoot(fulu.SignedBlockHeader!.Message!);
        server.Pool.Add(fuluRoot, fulu.SignedBlockHeader.Message!.Slot, fulu);

        Node client = CreateNode(spec);
        client.StatusHolder.CurrentStatus = status;

        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);

        client.Config.StaticPeers = PeerSessionNodes.LoopbackAddress(server.P2P).ToString();
        PeerManager peerManager = new(client.P2P, client.Config, client.StatusHolder, LimboLogs.Instance);
        await peerManager.RunMaintenanceRoundAsync(token);
        IBeaconSyncPeer peer = peerManager.GetBestPeers(gloasSlot).Single();
        long messagesSent = PeerManager.MessagesSentForTest(peer);

        IReadOnlyList<DataColumnSidecarGloas> byRoot = await peer.RequestGloasDataColumnSidecarsByRootAsync(
            [new DataColumnsByRootIdentifier { BlockRoot = heldRoot, Columns = [7, 5, 3] }, new DataColumnsByRootIdentifier { BlockRoot = pendingRoot, Columns = [3] }], token);
        Assert.That(byRoot.Select(static s => (s.BeaconBlockRoot, s.Index, s.Slot)), Is.EqualTo(new[] { (heldRoot, 7UL, gloasSlot), (heldRoot, 3UL, gloasSlot) }),
            "served verified Gloas columns in request order under the Gloas digest, never the pending candidate");
        Assert.That(PeerManager.MessagesSentForTest(peer), Is.EqualTo(messagesSent + 1), "the by-root ask counts as a message sent");

        IReadOnlyList<DataColumnSidecarGloas> byRange = await peer.RequestGloasDataColumnSidecarsByRangeAsync(gloasSlot, 1, [3], token);
        Assert.That(byRange.Select(static s => (s.BeaconBlockRoot, s.Index, s.Slot)), Is.EqualTo(new[] { (heldRoot, 3UL, gloasSlot) }),
            "served the canonical block's verified Gloas column by range, read in the Gloas shape");
        Assert.That(PeerManager.MessagesSentForTest(peer), Is.EqualTo(messagesSent + 2), "the by-range ask counts as a message sent");

        ISession toServer = await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token);
        IReadOnlyList<DataColumnSidecar> fuluByRoot = await client.P2P.RequestDataColumnSidecarsByRootAsync(toServer, [new DataColumnsByRootIdentifier { BlockRoot = fuluRoot, Columns = [3] }], token);
        Assert.That(fuluByRoot.Select(static s => s.Index), Is.EqualTo(new[] { 3UL }), "the Fulu dial still reads Fulu chunks");

        using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(token);
        quick.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.That(await peer.RequestGloasDataColumnSidecarsByRootAsync([], quick.Token), Is.Empty, "an empty ask returns at once, not after the response timeout");
        Assert.That(await client.P2P.RequestDataColumnSidecarsByRootAsync(toServer, [], quick.Token), Is.Empty, "an empty ask returns at once, not after the response timeout");
    }

    [Test]
    [CancelAfter(60_000)]
    public async Task Empty_by_root_requests_are_answered_with_no_chunks_at_once(CancellationToken token)
    {
        Node server = CreateNode();
        Node client = CreateNode();

        await using PeerHostScope hosts = new(client.P2P, server.P2P);
        await hosts.StartAsync(token, server.P2P, client.P2P);
        ISession toServer = await PeerSessionNodes.DialAsync(client.P2P, server.P2P, token);

        using CancellationTokenSource quick = CancellationTokenSource.CreateLinkedTokenSource(token);
        quick.CancelAfter(TimeSpan.FromSeconds(5));
        Assert.That(await client.P2P.RequestExecutionPayloadEnvelopesByRootAsync(toServer, [], quick.Token), Is.Empty, "envelopes by root");
        Assert.That(await client.P2P.RequestBlocksByRootAsync(toServer, [], quick.Token), Is.Empty, "blocks by root");
    }

    public enum ColumnDial
    {
        FuluByRange,
        GloasByRange,
        FuluByRoot,
        GloasByRoot,
    }

    [Test]
    public async Task Each_column_request_dials_its_protocol_asking_for_its_own_sidecar_shape([Values] ColumnDial dial)
    {
        ISession session = Substitute.For<ISession>();
        ForkedDataColumnSidecars empty = new([], []);
        session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(empty);
        session.DialAsync<DataColumnSidecarsByRootProtocol, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>(default, default).ReturnsForAnyArgs(empty);
        DataColumnsByRootIdentifier[] identifiers = [new() { BlockRoot = Hash256.Zero, Columns = [3] }];
        Node node = CreateNode();

        await using (node.P2P)
        {
            Task request = dial switch
            {
                ColumnDial.FuluByRange => node.P2P.RequestDataColumnSidecarsByRangeAsync(session, 1, 1, [3], default),
                ColumnDial.GloasByRange => node.P2P.RequestGloasDataColumnSidecarsByRangeAsync(session, 1, 1, [3], default),
                ColumnDial.FuluByRoot => node.P2P.RequestDataColumnSidecarsByRootAsync(session, identifiers, default),
                ColumnDial.GloasByRoot => node.P2P.RequestGloasDataColumnSidecarsByRootAsync(session, identifiers, default),
                _ => throw new ArgumentOutOfRangeException(nameof(dial)),
            };
            await request;
        }

        ICall call = session.ReceivedCalls().Single(static c => c.GetMethodInfo().Name == nameof(ISession.DialAsync));
        bool gloas = call.GetArguments()[0] switch
        {
            DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest> byRange => byRange.Gloas,
            DataColumnSidecarsDial<DataColumnsByRootIdentifier[]> byRoot => byRoot.Gloas,
            var other => throw new AssertionException($"Unexpected dial argument {other}"),
        };
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(call.GetMethodInfo().GetGenericArguments()[0],
            Is.EqualTo(dial is ColumnDial.FuluByRange or ColumnDial.GloasByRange ? typeof(DataColumnSidecarsByRangeProtocol) : typeof(DataColumnSidecarsByRootProtocol)));
        Assert.That(gloas, Is.EqualTo(dial is ColumnDial.GloasByRange or ColumnDial.GloasByRoot));
    }

    [Test]
    public async Task A_block_reply_that_fails_after_some_blocks_hands_those_blocks_to_the_caller([Values] bool delivered)
    {
        ForkedSignedBeaconBlock block = new ForkedSignedBeaconBlock.OfFulu(TestChain.CreateBlock(5, parentRoot: Hash256.Zero));
        ISession session = Substitute.For<ISession>();
        session.DialAsync<BeaconBlocksByRangeProtocolV2, BeaconBlocksByRangeDial, IReadOnlyList<ForkedSignedBeaconBlock>>(default, default).ReturnsForAnyArgs(call =>
        {
            if (delivered)
            {
                call.Arg<BeaconBlocksByRangeDial>().OnBlock!(block);
            }

            return Task.FromException<IReadOnlyList<ForkedSignedBeaconBlock>>(new Eth2ReqRespException("Truncated response chunk: Unable to read beyond the end of the stream."));
        });
        Node node = CreateNode();
        await using PeerHostScope hosts = new(node.P2P);
        Exception? thrown = await Assert.CatchAsync(async () => await node.P2P.RequestBlocksByRangeAsync(session, 5, 2, default));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That((thrown as PartialBlocksException)?.Received, delivered ? Is.EqualTo(new[] { block }) : Is.Null);
        Assert.That(thrown!.Message, Does.StartWith("Truncated response chunk"));
    }

    private static void AssertStatus(StatusMessageV2 actual, StatusMessageV2 expected)
    {
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(actual.ForkDigest, Is.EqualTo(expected.ForkDigest), "fork digest");
        Assert.That(actual.FinalizedRoot, Is.EqualTo(expected.FinalizedRoot), "finalized root");
        Assert.That(actual.FinalizedEpoch, Is.EqualTo(expected.FinalizedEpoch), "finalized epoch");
        Assert.That(actual.HeadRoot, Is.EqualTo(expected.HeadRoot), "head root");
        Assert.That(actual.HeadSlot, Is.EqualTo(expected.HeadSlot), "head slot");
        Assert.That(actual.EarliestAvailableSlot, Is.EqualTo(expected.EarliestAvailableSlot), "earliest available slot");
    }

    private record Node(BeaconP2P P2P, BeaconChainStore Store, BeaconChainStatusHolder StatusHolder, LocalMetadataSource MetadataSource, BeaconChainConfig Config, DataColumnSidecarPool Pool);

    private static Node CreateNode(BeaconChainSpec? spec = null, bool lightClient = false)
    {
        spec ??= Spec;
        BeaconChainConfig config = new() { P2PPort = 0 };
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        BeaconChainStatusHolder statusHolder = new(spec, Timestamper.Default);
        LocalMetadataSource metadataSource = new();
        DataColumnSidecarPool pool = new();
        BeaconP2P p2p = new(config, spec, store, statusHolder, metadataSource, pool, new ExecutionPayloadEnvelopePool(), LimboLogs.Instance,
            lightClientRequests: lightClient);
        return new Node(p2p, store, statusHolder, metadataSource, config, pool);
    }
}
