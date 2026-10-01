// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Network.Libp2p;
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

    /// <summary>A static peer the router holds no gossip connection to, as after any disconnect it does not redial, is connected by the static peer check.</summary>
    /// <remarks>The router never redials a peer whose reconnection it suppressed, and discovering a known peer again does nothing.</remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_static_peer_without_a_gossip_connection_is_connected_by_the_static_peer_check(CancellationToken token)
    {
        // Both routers' own redials are off and nothing is discovered, so only the static peer check can connect the peer.
        await using Host sequencer = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        await using Host node = await Host.StartAsync(token, static settings => settings.ReconnectionPeriod = Timeout.Infinite);
        PeerId sequencerId = sequencer.Peer.Identity.PeerId;
        IRoutingStateContainer routing = node.Router;
        Assert.That(routing.ConnectedPeers, Does.Not.Contain(sequencerId), "fixture: the peer starts unconnected");

        using StaticPeerKeeper keeper = new(node.Peer, routing, [sequencer.Address], LimboLogs.Instance.GetClassLogger<OptimismGossipLoopbackTests>());
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
            (_, dialToken) =>
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

    private sealed class Host(ServiceProvider services, ILocalPeer peer) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public ILocalPeer Peer => peer;

        public PubsubRouter Router => services.GetRequiredService<PubsubRouter>();

        // The libp2p dial needs the peer id, which a listen address may not carry.
        public Multiaddress Address => Multiaddress.Decode($"{peer.ListenAddresses.First().ToString().Split("/p2p/")[0]}/p2p/{peer.Identity.PeerId}");

        public static async Task<Host> StartAsync(CancellationToken token, Action<PubsubSettings>? configure = null)
        {
            PubsubSettings settings = OptimismCLP2P.CreatePubsubSettings(BlocksTopic);
            configure?.Invoke(settings);
            ServiceProvider services = new ServiceCollection()
                .AddLibp2p(static builder => builder.WithPubsub())
                .AddSingleton(settings)
                .BuildServiceProvider();
            ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create();
            await peer.StartListenAsync([Multiaddress.Decode("/ip4/127.0.0.1/tcp/0")], token);
            await services.GetRequiredService<PubsubRouter>().StartAsync(peer, token);
            return new Host(services, peer);
        }

        public async ValueTask DisposeAsync()
        {
            await peer.DisposeAsync();
            await services.DisposeAsync();
        }
    }
}
