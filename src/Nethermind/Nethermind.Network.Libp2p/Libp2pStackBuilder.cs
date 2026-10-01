// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Multiformats.Address;
using Multiformats.Address.Protocols;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Network.Libp2p;

/// <summary>The libp2p stack the node's libp2p hosts run on: TCP, Noise, yamux, identify, ping and optionally gossipsub.</summary>
/// <remarks>Every yamux channel passes through <see cref="ContiguousChunkProtocol"/> before multistream. Built from the component packages, so the node ships none of the transports the meta package adds (WebRTC, WebSockets, relay, QUIC, TLS) or their dependencies.</remarks>
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
        Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()], [Get<YamuxProtocol>()], [Get<ContiguousChunkProtocol>()], appSelector);

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

/// <summary>Resolves a DNS host name to its addresses of <paramref name="family"/>.</summary>
public delegate Task<IPAddress[]> HostResolver(string host, AddressFamily family, CancellationToken token);

/// <summary>Creates peers that run identify on every new session and push it when their listen addresses change.</summary>
/// <param name="resolveHost">Resolves DNS names in dialed addresses; the system resolver when null.</param>
public class Libp2pStackPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null,
    HostResolver? resolveHost = null)
    : PeerFactory(protocolStackSettings, peerStore, loggerFactory: loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) =>
        new IdentifyingPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), PeerStore, protocolStackSettings, identifyNotifier, LoggerFactory,
            resolveHost ?? (static (host, family, token) => Dns.GetHostAddressesAsync(host, family, token)));

    /// <remarks>A dial through <see cref="ILocalPeer"/> reaches the library only with resolved TCP addresses of one peer id: Nethermind.Libp2p 1.0.0
    /// keeps a dial that fails before its first await as the pending dial of that peer id for good, and a failed DNS lookup, an address of a
    /// transport this stack lacks, or addresses of several peer ids fail that way. Any peer can announce such addresses for any peer id
    /// through pubsub peer discovery.</remarks>
    private sealed class IdentifyingPeer : LocalPeer, ILocalPeer
    {
        private readonly HostResolver _resolveHost;

        public IdentifyingPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory,
            HostResolver resolveHost)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory)
        {
            _resolveHost = resolveHost;
            notifier.TrackChanges(this);
        }

        protected override Task ConnectedTo(ISession session, bool isDialer) => session.DialAsync<IdentifyProtocol>();

        Task<ISession> ILocalPeer.DialAsync(Multiaddress addr, CancellationToken token) => DialTcpAsync([addr], token);

        Task<ISession> ILocalPeer.DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token) => DialTcpAsync(samePeerAddrs, token);

        Task<ISession> ILocalPeer.DialAsync(PeerId peerId, CancellationToken token) =>
            _peerStore?.GetPeerInfo(peerId).Addrs is { Count: > 0 } addrs ? DialTcpAsync([.. addrs], token) : DialAsync(peerId, token);

        private async Task<ISession> DialTcpAsync(Multiaddress[] addrs, CancellationToken token)
        {
            PeerId? peerId = addrs.FirstOrDefault()?.GetPeerId();
            if (peerId is null || addrs.Any(addr => addr.GetPeerId() != peerId))
            {
                throw new Libp2pException("A dial needs addresses of one peer id");
            }

            if (Sessions.FirstOrDefault(session => session.State.RemotePeerId == peerId) is { } existing)
            {
                return existing;
            }

            List<Multiaddress> tcp = [];
            foreach (Multiaddress addr in addrs)
            {
                foreach (Multiaddress resolved in await ResolveAsync(addr, token))
                {
                    if (resolved.Has<TCP>() && (resolved.Has<IP4>() || resolved.Has<IP6>()) && !resolved.Has<WebSocket>() && !resolved.Has<WebSocketSecure>())
                    {
                        tcp.Add(resolved);
                    }
                }
            }

            if (tcp.Count == 0)
            {
                throw new Libp2pException($"No TCP address to dial {peerId}");
            }

            return await DialAsync([.. tcp], token);
        }

        // A name that does not resolve leaves the other addresses of the peer to dial.
        private async Task<Multiaddress[]> ResolveAsync(Multiaddress addr, CancellationToken token)
        {
            try
            {
                if (addr.Has<DNS4>())
                {
                    IPAddress[] ips = await _resolveHost(addr.Get<DNS4>().ToString(), AddressFamily.InterNetwork, token);
                    return [.. ips.Select(ip => addr.Clone().Replace<DNS4, IP4>(ip))];
                }

                if (addr.Has<DNS6>())
                {
                    IPAddress[] ips = await _resolveHost(addr.Get<DNS6>().ToString(), AddressFamily.InterNetworkV6, token);
                    return [.. ips.Select(ip => addr.Clone().Replace<DNS6, IP6>(ip))];
                }

                if (addr.Has<DNS>())
                {
                    IPAddress[] ips = await _resolveHost(addr.Get<DNS>().ToString(), AddressFamily.Unspecified, token);
                    return [.. ips.Select(ip => ip.AddressFamily == AddressFamily.InterNetworkV6 ? addr.Clone().Replace<DNS, IP6>(ip) : addr.Clone().Replace<DNS, IP4>(ip))];
                }

                return [addr];
            }
            catch (SocketException)
            {
                return [];
            }
        }
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
