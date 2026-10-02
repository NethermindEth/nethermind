// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Core;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Network;
using Nethermind.Network.Libp2p;
using Nethermind.Optimism.CL;
using Nethermind.Optimism.CL.P2P;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class OptimismGossipLoopbackTests
{
    private const string BlocksTopic = "/optimism/10/2/blocks";

    /// <summary>rollup-node-p2p.md: block gossip may carry up to 10 MiB, so a block of any legal size crosses a real session whole.</summary>
    /// <remarks>An RPC over the receiver's bound, or one truncated in the yamux channel, ends the read loop and disconnects the peer.</remarks>
    [TestCase(64, TestName = "A small block message reaches the other host")]
    [TestCase(280 * 1024, TestName = "A block message over one yamux window reaches the other host")]
    [TestCase(1024 * 1024 + 4096, TestName = "A block message over one mebibyte reaches the other host")]
    [CancelAfter(60_000)]
    public Task A_block_message_reaches_the_other_host(int size, CancellationToken token) => PublishAndReceiveAsync(size, token);

    /// <summary>The receiver reads an RPC and an IWANT answer up to max_compressed_len(10 MiB) + 1024 bytes, not the library's 1 MiB and 512 KiB.</summary>
    [Test]
    public void Block_gossip_bounds_admit_the_op_stack_maximum()
    {
        PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(settings.MaxRpcBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize).And.GreaterThan(10 * 1024 * 1024));
            Assert.That(settings.MaxIwantResponseBytes, Is.EqualTo(Eth2MessageId.MaxMessageSize));
        }
    }

    private static async Task PublishAndReceiveAsync(int size, CancellationToken token)
    {
        await using Host publisher = await Host.StartAsync(token);
        await using Host subscriber = await Host.StartAsync(token);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.Router.GetTopic(BlocksTopic).OnMessage += (_, data) => received.TrySetResult(data);
        ITopic topic = publisher.Router.GetTopic(BlocksTopic);
        subscriber.Services.GetRequiredService<PeerStore>().Discover([publisher.Address]);

        byte[] block = new byte[size];
        Random.Shared.NextBytes(block);
        while (!received.Task.IsCompleted)
        {
            // The publisher sends only once it has learnt the subscription, so it repeats until the subscriber has the block.
            token.ThrowIfCancellationRequested();
            topic.Publish(block);
            await Task.WhenAny(received.Task, Task.Delay(500, token));
        }

        Assert.That(await received.Task, Is.EqualTo(block));
    }

    /// <summary>A static peer the router holds no gossip connection to is connected by the static peer check, at first and again after its
    /// connection closed.</summary>
    /// <remarks>The router never redials a peer whose reconnection it suppressed, and discovering a known peer again does nothing.</remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_is_connected_by_the_static_peer_check_again_after_a_disconnect(CancellationToken token)
    {
        // Both routers' own redials are off and nothing is discovered, so only the static peer check can connect the peer.
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        IRoutingStateContainer routing = node.Router;
        Assert.That(routing.ConnectedPeers, Does.Not.Contain(sequencerId), "fixture: the peer starts unconnected");

        using StaticPeerKeeper keeper = new(node.Peer, routing, [sequencer.Address], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
        await KeepUntilConnectedAsync(keeper, node, routing, sequencerId, token);

        foreach (Host host in new[] { node, sequencer })
        {
            foreach (ISession session in ((LocalPeer)host.Peer).Sessions.ToArray())
            {
                await session.DisconnectAsync();
            }
        }

        // The router drops a peer only when its gossip channels end, so a channel left open below would keep it here.
        using (CancellationTokenSource dropped = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            dropped.CancelAfter(TimeSpan.FromSeconds(15));
            while (routing.ConnectedPeers.Contains(sequencerId))
            {
                if (dropped.IsCancellationRequested)
                {
                    Assert.Fail($"the router kept the closed peer ({((LocalPeer)node.Peer).Sessions.Count} sessions)");
                }

                await Task.Delay(50, CancellationToken.None);
            }
        }

        await KeepUntilConnectedAsync(keeper, node, routing, sequencerId, token);
    }

    /// <summary>A gossip dial that has not connected the peer by the next check is cancelled before another starts, so a peer that stalls
    /// in negotiation holds one gossip channel, not one per check.</summary>
    [Test]
    public async Task A_stalled_gossip_dial_is_cancelled_before_the_next_check_dials_again()
    {
        PeerId peerId = new Nethermind.Libp2p.Core.Identity().PeerId;
        Multiaddress address = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{peerId}");
        ILocalPeer localPeer = Substitute.For<ILocalPeer>();
        localPeer.DialAsync(Arg.Any<Multiaddress>(), Arg.Any<CancellationToken>()).Returns(Substitute.For<ISession>());
        IRoutingStateContainer router = Substitute.For<IRoutingStateContainer>();
        router.ConnectedPeers.Returns([]);
        List<CancellationToken> dials = [];
        List<bool> earlierCancelledAtStart = [];
        using StaticPeerKeeper keeper = new(localPeer, router, [address], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>(),
            openGossip: (_, dialToken) =>
            {
                earlierCancelledAtStart.Add(dials.All(static dial => dial.IsCancellationRequested));
                dials.Add(dialToken);
                return Task.Delay(Timeout.Infinite, dialToken);
            });

        for (int check = 0; check < 3; check++)
        {
            await keeper.CheckAsync(CancellationToken.None);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(earlierCancelledAtStart, Is.EqualTo(new[] { true, true, true }), "every earlier dial was cancelled before the next started");
            Assert.That(dials.Select(static dial => dial.IsCancellationRequested), Is.EqualTo(new[] { true, true, false }));
        }
    }

    /// <summary>A dial that cannot start, to addresses any peer can announce for another through pubsub peer discovery, does not keep that
    /// peer from being connected later.</summary>
    /// <remarks>Nethermind.Libp2p 1.0.0 keeps a dial that fails before its first await as the pending dial of the peer id for good.</remarks>
    [TestCase("/dns4/sequencer.invalid/tcp/{port}/p2p/{id}", TestName = "A name whose first lookup fails")]
    [TestCase("/dnsaddr/sequencer.invalid/p2p/{id}", TestName = "A dnsaddr name")]
    [TestCase("/ip4/127.0.0.1/tcp/{port}/dnsaddr/sequencer.invalid/p2p/{id}", TestName = "A dnsaddr name after an address")]
    [TestCase("/ip4/127.0.0.1/tcp/{port}/p2p/{id}|/ip4/127.0.0.1/tcp/{port}/p2p/{other}", TestName = "Addresses of two peer ids")]
    [CancelAfter(60_000)]
    public async Task A_dial_that_cannot_start_does_not_lose_the_peer(string announced, CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite, new LoopbackDns(txt: ""));
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        string port = sequencer.Address.ToString().Split('/')[4];
        Multiaddress[] poison = [.. announced.Split('|').Select(address => Multiaddress.Decode(address
            .Replace("{port}", port).Replace("{id}", sequencerId.ToString()).Replace("{other}", new Nethermind.Libp2p.Core.Identity().PeerId.ToString())))];

        Assert.That(async () => await node.Peer.DialAsync(poison, token), Throws.Exception, "fixture: the dial fails");

        Multiaddress named = Multiaddress.Decode($"/dns4/sequencer.invalid/tcp/{port}/p2p/{sequencerId}");
        using StaticPeerKeeper keeper = new(node.Peer, node.Router, [named], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
        await KeepUntilConnectedAsync(keeper, node, node.Router, sequencerId, token);
    }

    /// <summary>A static peer named by a host the hosts file maps is connected, as the operating system resolver finds it.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_named_in_the_hosts_file_is_connected(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        string port = sequencer.Address.ToString().Split('/')[4];

        Multiaddress named = Multiaddress.Decode($"/dns4/localhost/tcp/{port}/p2p/{sequencerId}");
        using StaticPeerKeeper keeper = new(node.Peer, node.Router, [named], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
        await KeepUntilConnectedAsync(keeper, node, node.Router, sequencerId, token);
    }

    /// <summary>A static peer named by /dns, whose name has IPv4 addresses only, is connected at its IPv4 address.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_named_by_dns_with_ipv4_only_is_connected(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite, new LoopbackDns(txt: "", noIpv6: true));
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        string port = sequencer.Address.ToString().Split('/')[4];

        Multiaddress named = Multiaddress.Decode($"/dns/sequencer.test/tcp/{port}/p2p/{sequencerId}");
        using StaticPeerKeeper keeper = new(node.Peer, node.Router, [named], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
        await KeepUntilConnectedAsync(keeper, node, node.Router, sequencerId, token);
    }

    /// <summary>A name whose DNS server never answers is given up within the resolution deadline, and the other addresses of the peer are still dialed.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_name_that_never_resolves_does_not_hold_the_other_addresses(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token);
        await using Host node = await Host.StartAsync(token, dnsLookup: new LoopbackDns(txt: "", silent: true));
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        string port = sequencer.Address.ToString().Split('/')[4];
        Multiaddress[] addresses = [Multiaddress.Decode($"/dns4/silent.test/tcp/{port}/p2p/{sequencerId}"), sequencer.Address];

        // Ten seconds of resolution deadline, then the dial of the literal address.
        ISession session = await node.Peer.DialAsync(addresses, token).WaitAsync(TimeSpan.FromSeconds(25), token);

        Assert.That(session.RemoteAddress.GetPeerId(), Is.EqualTo(sequencerId));
    }

    /// <summary>A static peer named by a dnsaddr TXT record is connected at the address the record names.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_named_by_dnsaddr_is_connected(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite,
            new LoopbackDns(txt: $"dnsaddr={sequencer.Address}"));

        Multiaddress named = Multiaddress.Decode($"/dnsaddr/sequencer.test/p2p/{sequencerId}");
        using StaticPeerKeeper keeper = new(node.Peer, node.Router, [named], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
        await KeepUntilConnectedAsync(keeper, node, node.Router, sequencerId, token);
    }

    /// <summary>A dial its caller stopped waiting for, which then fails, leaves no unobserved failure behind.</summary>
    [Test]
    [NonParallelizable]
    [CancelAfter(60_000)]
    public async Task An_abandoned_dial_that_fails_is_observed(CancellationToken token)
    {
        await using Host node = await Host.StartAsync(token);
        using TcpListener closed = new(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        string refusing = $"/ip4/127.0.0.1/tcp/{port}/p2p/{new Nethermind.Libp2p.Core.Identity().PeerId}";
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
            await AbandonAsync(node.Peer, Multiaddress.Decode(refusing), token);
            // The library dial fails within milliseconds on a refused connection; its task is then collectable.
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
    private static async Task AbandonAsync(ILocalPeer peer, Multiaddress address, CancellationToken token)
    {
        using CancellationTokenSource abandoned = CancellationTokenSource.CreateLinkedTokenSource(token);
        await abandoned.CancelAsync();
        Assert.That(async () => await peer.DialAsync(address, abandoned.Token), Throws.InstanceOf<OperationCanceledException>(), "fixture: the caller stops waiting");
    }

    /// <summary>A dial its caller cancelled before it began does not keep the peer from being dialed afterwards.</summary>
    /// <remarks>Nethermind.Libp2p 1.0.0 keeps a dial cancelled before its first await as the pending dial of the peer id for good.</remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_cancelled_dial_does_not_lose_the_peer(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token);
        await using Host node = await Host.StartAsync(token);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();

        Assert.That(async () => await node.Peer.DialAsync(sequencer.Address, cancelled.Token), Throws.InstanceOf<OperationCanceledException>(), "fixture: the dial is cancelled");
        Assert.That(await node.Peer.DialAsync(sequencer.Address, token).WaitAsync(TimeSpan.FromSeconds(20), token), Is.Not.Null);
    }

    /// <summary>A dial still running when its peer is disposed leaves no session open: the library dial outlives its caller's token.</summary>
    [Test]
    [CancelAfter(90_000)]
    public async Task A_peer_disposed_during_a_dial_leaves_no_session_open(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token);
        await using Host node = await Host.StartAsync(token);
        await AssertNoSessionAfterShutdownAsync(node.Peer, sequencer, () => node.ShutDownAsync(), token);
    }

    /// <summary>A dial still running when the CL P2P host shuts down leaves no session open.</summary>
    [Test]
    [CancelAfter(90_000)]
    public async Task A_host_shut_down_during_a_dial_leaves_no_session_open(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token);
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        OptimismCLP2P p2p = new(Substitute.For<IExecutionEngineManager>(), 10, [], new OptimismConfig { ClP2PHost = "127.0.0.1", ClP2PPort = 0 }, Address.Zero,
            Substitute.For<ITimestamper>(), Substitute.For<IIPResolver>(), LimboLogs.Instance);
        Task run = p2p.Run(stop.Token);
        ILocalPeer? peer = null;
        while ((peer = p2p.LocalPeerForTest) is null || peer.ListenAddresses.Count == 0)
        {
            await Task.Delay(50, token);
        }

        try
        {
            // As OptimismCL shuts down: the run token stops the listener, then the host is disposed.
            await AssertNoSessionAfterShutdownAsync(peer, sequencer, async () => { await stop.CancelAsync(); p2p.Dispose(); }, token);
        }
        finally
        {
            await stop.CancelAsync();
            await run;
        }
    }

    // Starts a dial from dialer, stops waiting for it, shuts down, and checks the session the dial opens at remote is closed.
    private static async Task AssertNoSessionAfterShutdownAsync(ILocalPeer dialer, Host remote, Func<ValueTask> shutdown, CancellationToken token)
    {
        LocalPeer remotePeer = (LocalPeer)remote.Peer;
        TaskCompletionSource reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        remotePeer.Sessions.CollectionChanged += (_, change) =>
        {
            if (change.Action == NotifyCollectionChangedAction.Add) reached.TrySetResult();
        };
        using CancellationTokenSource abandoned = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ISession> dial = dialer.DialAsync(remote.Address, abandoned.Token);

        await abandoned.CancelAsync();
        await shutdown();
        try
        {
            await dial;
        }
        catch (OperationCanceledException)
        {
        }

        await reached.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        Assert.That(dial.IsCanceled, Is.True, "fixture: the caller stopped waiting before the dial finished");

        // The library ends a dial within 15 s, and a remote that loses a connection mid-handshake drops it up to 30 s later;
        // a session left open was still open after 40 s.
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(45));
        while (remotePeer.Sessions.Count > 0 && !bounded.IsCancellationRequested)
        {
            await Task.Delay(50, CancellationToken.None);
        }

        Assert.That(remotePeer.Sessions, Is.Empty, "the dial finished after shutdown and its session stayed open");
    }

    /// <summary>A connected peer whose stored addresses were replaced by a set of two peer ids is still dialed by its existing session.</summary>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_connected_peer_is_dialed_by_its_session_whatever_its_stored_addresses(CancellationToken token)
    {
        await using Host sequencer = await Host.StartAsync(token);
        await using Host node = await Host.StartAsync(token);
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        ISession session = await node.Peer.DialAsync(sequencer.Address, token);
        Multiaddress other = Multiaddress.Decode($"/ip4/127.0.0.1/tcp/1/p2p/{new Nethermind.Libp2p.Core.Identity().PeerId}");

        node.Services.GetRequiredService<PeerStore>().Discover([sequencer.Address, other]);

        Assert.That(await node.Peer.DialAsync(sequencerId, token), Is.SameAs(session));
    }

    // Runs the static peer check as its timer does, more often: the peer can refuse a dial while it still holds an earlier session.
    private static async Task KeepUntilConnectedAsync(StaticPeerKeeper keeper, Host node, IRoutingStateContainer routing, PeerId peerId, CancellationToken token)
    {
        using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(20));
        while (!routing.ConnectedPeers.Contains(peerId))
        {
            if (bounded.IsCancellationRequested)
            {
                Assert.Fail($"the static peer check never connected the peer ({((LocalPeer)node.Peer).Sessions.Count} sessions)");
            }

            await keeper.CheckAsync(bounded.Token);
            await Task.Delay(200, CancellationToken.None);
        }
    }

    /// <summary>Answers every name with the loopback address and <paramref name="txt"/>, after failing the first query as a lookup that times out does.</summary>
    private sealed class LoopbackDns(string txt, bool noIpv6 = false, bool silent = false) : IDnsLookup
    {
        private int _queries;

        public Task<IEnumerable<string>> QueryTxtAsync(string name) => Answer<string>([txt]);

        public Task<IEnumerable<IPAddress>> QueryAAsync(string name) => Answer<IPAddress>([IPAddress.Loopback]);

        // The operating system resolver fails a query for a family the name has no addresses of.
        public Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) =>
            noIpv6 ? Task.FromException<IEnumerable<IPAddress>>(new SocketException((int)SocketError.NoData)) : Answer<IPAddress>([]);

        private Task<IEnumerable<T>> Answer<T>(T[] records) => silent ? new TaskCompletionSource<IEnumerable<T>>(TaskCreationOptions.RunContinuationsAsynchronously).Task
            : Interlocked.Increment(ref _queries) == 1
            ? Task.FromException<IEnumerable<T>>(new SocketException((int)SocketError.TimedOut))
            : Task.FromResult<IEnumerable<T>>(records);
    }

    private sealed class Host(ServiceProvider services, ILocalPeer peer, CancellationTokenSource listening) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public ILocalPeer Peer => peer;

        public PubsubRouter Router => services.GetRequiredService<PubsubRouter>();

        // The libp2p dial needs the peer id, which a listen address may not carry.
        public Multiaddress Address => Multiaddress.Decode($"{peer.ListenAddresses.First().ToString().Split("/p2p/")[0]}/p2p/{peer.Identity.PeerId}");

        public static async Task<Host> StartAsync(CancellationToken token, Action<PubsubSettings>? configure = null, IDnsLookup? dnsLookup = null)
        {
            PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);
            configure?.Invoke(settings);
            IServiceCollection collection = new ServiceCollection()
                .AddLibp2p(static builder => builder.WithPubsub())
                .AddSingleton(settings);
            if (dnsLookup is not null)
            {
                collection.AddSingleton(dnsLookup);
            }

            ServiceProvider services = collection.BuildServiceProvider();
            ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create();
            CancellationTokenSource listening = CancellationTokenSource.CreateLinkedTokenSource(token);
            await peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], listening.Token);
            await services.GetRequiredService<PubsubRouter>().StartAsync(peer, token);
            return new Host(services, peer, listening);
        }

        /// <summary>Shuts the peer down as a host does: the listener stops with the run token, then the peer is disposed.</summary>
        public async ValueTask ShutDownAsync()
        {
            await listening.CancelAsync();
            await peer.DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await ShutDownAsync();
            await services.DisposeAsync();
            listening.Dispose();
        }
    }
}
