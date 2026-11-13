// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Optimism.CL.P2P;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Optimism.Test.CL;

public class OptimismCLP2PTests
{
    [Test]
    public async Task Gossip_settings_preserve_large_payloads_with_the_registered_peer_factory()
    {
        PubsubSettings settings = OptimismCLP2P.GossipSettings(static _ => new MessageId(new byte[20]));
        int largestPayload = Snappy.GetMaxCompressedLength(10 * 1024 * 1024);
        await using ServiceProvider services = new ServiceCollection()
            .AddLibp2p(static builder => builder.WithPubsub())
            .AddSingleton(settings)
            .BuildServiceProvider();
        await using ILocalPeer peer = services.GetRequiredService<IPeerFactory>().Create();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Identity.PeerId, Is.Not.Null);
            Assert.That(settings.MaxRpcBytes, Is.GreaterThan(largestPayload));
            Assert.That(settings.MaxOutboundRpcBytesPerPeer, Is.GreaterThan(settings.MaxRpcBytes));
            Assert.That(settings.MaxIwantResponseBytes, Is.GreaterThanOrEqualTo(settings.MaxRpcBytes));
            Assert.That(settings.MaxIncomingMessageIdRpcBytes, Is.GreaterThanOrEqualTo(settings.MaxRpcBytes));
        }
    }
}
