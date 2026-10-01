// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Network.Libp2p;
using Nethermind.Optimism.CL.P2P;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class OptimismGossipLoopbackTests
{
    private const string BlocksTopic = "/optimism/10/2/blocks";

    /// <summary>Block gossip on the Optimism settings crosses a real TCP, Noise and yamux session.</summary>
    [TestCase(64, TestName = "A small block message reaches the other host")]
    [TestCase(280 * 1024, TestName = "A block message over one yamux window reaches the other host")]
    [CancelAfter(60_000)]
    public Task A_block_message_reaches_the_other_host(int size, CancellationToken token) => PublishAndReceiveAsync(size, token);

    /// <summary>rollup-node-p2p.md: block gossip may carry up to 10 MiB, so a block over the library's 1 MiB RPC bound still arrives.</summary>
    /// <remarks>An RPC over the receiver's bound ends its read loop and disconnects the peer without reconnecting, losing the sequencer until restart.</remarks>
    [Test]
    [CancelAfter(60_000)]
    [Explicit("Nethermind.Libp2p 1.0.0 Channel.ReadAsync appends only the first segment of each later chunk, so an RPC over about 320 KiB arrives truncated")]
    public Task A_block_message_over_one_mebibyte_reaches_the_other_host(CancellationToken token) => PublishAndReceiveAsync(1024 * 1024 + 4096, token);

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

    private sealed class Host(ServiceProvider services, ILocalPeer peer) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public PubsubRouter Router => services.GetRequiredService<PubsubRouter>();

        // The libp2p dial needs the peer id, which a listen address may not carry.
        public Multiaddress Address => Multiaddress.Decode($"{peer.ListenAddresses.First().ToString().Split("/p2p/")[0]}/p2p/{peer.Identity.PeerId}");

        public static async Task<Host> StartAsync(CancellationToken token)
        {
            ServiceProvider services = new ServiceCollection()
                .AddLibp2p(static builder => builder.WithPubsub())
                .AddSingleton(OptimismCLP2P.CreatePubsubSettings(BlocksTopic))
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
