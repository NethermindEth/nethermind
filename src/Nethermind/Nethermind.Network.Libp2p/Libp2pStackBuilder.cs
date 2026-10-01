// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Network.Libp2p;

/// <summary>The libp2p stack the node's libp2p hosts run on: TCP, Noise, yamux, identify, ping and optionally gossipsub.</summary>
/// <remarks>Built from the component packages, so the node ships none of the transports the meta package adds (WebRTC, WebSockets, relay, QUIC, TLS) or their dependencies.</remarks>
public sealed class Libp2pStackBuilder(IServiceProvider? serviceProvider = null)
    : PeerFactoryBuilderBase<Libp2pStackBuilder, Libp2pStackPeerFactory>(serviceProvider)
{
    private bool _pubsub;

    /// <summary>Adds gossipsub (v1.0 to v1.3) and floodsub to the protocols every session offers.</summary>
    public Libp2pStackBuilder WithPubsub()
    {
        _pubsub = true;
        return this;
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NoiseProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(YamuxProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MultistreamProtocol))]
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
        ProtocolRef[] appSelector = [Get<MultistreamProtocol>()];
        Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()], [Get<YamuxProtocol>()], appSelector);

        List<ProtocolRef> apps = [Get<IdentifyProtocol>(), Get<IdentifyPushProtocol>(), Get<PingProtocol>(), .. additionalProtocols];
        if (_pubsub)
        {
            apps.AddRange([Get<GossipsubProtocolV13>(), Get<GossipsubProtocolV12>(), Get<GossipsubProtocolV11>(), Get<GossipsubProtocol>(), Get<FloodsubProtocol>()]);
        }

        // A typed array is one choice of protocols; a collection expression would bind to the params array and chain them instead.
        ProtocolRef[] choices = [.. apps];
        Connect(appSelector, choices);
        return [tcp];
    }
}

/// <summary>Creates peers that run identify on every new session and push it when their listen addresses change.</summary>
public class Libp2pStackPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null)
    : PeerFactory(protocolStackSettings, peerStore, loggerFactory: loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) =>
        new IdentifyingPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), PeerStore, protocolStackSettings, identifyNotifier, LoggerFactory);

    private sealed class IdentifyingPeer : LocalPeer
    {
        public IdentifyingPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory) => notifier.TrackChanges(this);

        protected override Task ConnectedTo(ISession session, bool isDialer) => session.DialAsync<IdentifyProtocol>();
    }
}

public static class Libp2pServiceCollectionExtensions
{
    /// <summary>Registers a libp2p host on <see cref="Libp2pStackBuilder"/>; <paramref name="setup"/> adds protocols to it.</summary>
    public static IServiceCollection AddLibp2p(this IServiceCollection services, Func<Libp2pStackBuilder, IPeerFactoryBuilder>? setup = null) => services
        .AddSingleton<IProtocolStackSettings, ProtocolStackSettings>()
        .AddSingleton(sp =>
        {
            Libp2pStackBuilder builder = ActivatorUtilities.CreateInstance<Libp2pStackBuilder>(sp);
            return setup?.Invoke(builder) ?? builder;
        })
        .AddSingleton(static sp => sp.GetRequiredService<IPeerFactoryBuilder>().Build())
        .AddSingleton<MultiplexerSettings>()
        .AddSingleton<PubsubRouter>()
        .AddSingleton<PeerStore>()
        .AddSingleton<IdentifyNotifier>();
}
