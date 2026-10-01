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

    /// <summary>rollup-node-p2p.md: block gossip may carry up to 10 MiB, so a block over the library's 1 MiB RPC bound still arrives.</summary>
    /// <remarks>An RPC over the receiver's bound ends its read loop and disconnects the peer without reconnecting, losing the sequencer until restart.</remarks>
    [Test]
    [CancelAfter(60_000)]
    public async Task A_block_message_over_one_mebibyte_reaches_the_other_host(CancellationToken token)
    {
        await using Host publisher = await Host.StartAsync(token);
        await using Host subscriber = await Host.StartAsync(token);
        TaskCompletionSource<byte[]> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.Router.GetTopic(BlocksTopic).OnMessage += (_, data) => received.TrySetResult(data);
        ITopic topic = publisher.Router.GetTopic(BlocksTopic);
        subscriber.Services.GetRequiredService<PeerStore>().Discover([publisher.Address]);

        byte[] block = new byte[1024 * 1024 + 4096];
        Random.Shared.NextBytes(block);
        while (!received.Task.IsCompleted)
        {
            // The publisher sends only once it has learnt the subscription, so it repeats until the subscriber has the block.
            topic.Publish(block);
            await Task.WhenAny(received.Task, Task.Delay(500, token));
        }

        Assert.That(await received.Task, Is.EqualTo(block));
    }

    private sealed class Host(ServiceProvider services, ILocalPeer peer) : IAsyncDisposable
    {
        public ServiceProvider Services => services;

        public PubsubRouter Router => services.GetRequiredService<PubsubRouter>();

        public Multiaddress Address => peer.ListenAddresses.First();

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
