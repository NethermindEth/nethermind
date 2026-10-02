// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Net;
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

/// <summary>Creates peers that run identify on every new session and push it when their listen addresses change.</summary>
/// <param name="dnsLookup">Answers the DNS queries that resolve names in dialed addresses; DnsClient when null.</param>
public class Libp2pStackPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null,
    IDnsLookup? dnsLookup = null)
    : PeerFactory(protocolStackSettings, peerStore, loggerFactory: loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) =>
        new IdentifyingPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), PeerStore, protocolStackSettings, identifyNotifier, LoggerFactory,
            dnsLookup ?? new DnsClientLookup());

    /// <remarks>Dials through <see cref="ILocalPeer"/> reach the library only with resolved TCP addresses of one peer id: Nethermind.Libp2p 1.0.0
    /// keeps a dial that fails before its first await, as such addresses make it, as that peer id's pending dial for good.</remarks>
    private sealed class IdentifyingPeer : LocalPeer, ILocalPeer
    {
        // A TXT record may name another dnsaddr, and the library dials every address of a peer at once.
        private const int MaxDnsQueries = 32;
        private const int MaxDialAddresses = 16;

        private readonly IDnsLookup _dnsLookup;
        private int _disposed;

        public IdentifyingPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory,
            IDnsLookup dnsLookup)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory)
        {
            _dnsLookup = dnsLookup;
            notifier.TrackChanges(this);
        }

        protected override Task ConnectedTo(ISession session, bool isDialer) => session.DialAsync<IdentifyProtocol>();

        Task<ISession> ILocalPeer.DialAsync(Multiaddress addr, CancellationToken token) => DialTcpAsync(addr.GetPeerId(), [addr], token);

        Task<ISession> ILocalPeer.DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token) =>
            DialTcpAsync(samePeerAddrs.FirstOrDefault()?.GetPeerId(), samePeerAddrs, token);

        Task<ISession> ILocalPeer.DialAsync(PeerId peerId, CancellationToken token) =>
            FindSession(peerId) is { } existing ? Task.FromResult<ISession>(existing)
            : DialTcpAsync(peerId, _peerStore?.GetPeerInfo(peerId).Addrs?.ToArray() ?? [], token);

        private Session? FindSession(PeerId peerId) => Sessions.FirstOrDefault(session => session.State.RemotePeerId == peerId);

        private async Task<ISession> DialTcpAsync(PeerId? peerId, Multiaddress[] addrs, CancellationToken token)
        {
            if (peerId is null)
            {
                throw new Libp2pException("A dial needs addresses with a peer id");
            }

            if (FindSession(peerId) is { } existing)
            {
                return existing;
            }

            if (addrs.Any(addr => addr.GetPeerId() != peerId))
            {
                throw new Libp2pException($"A dial to {peerId} has addresses of another peer id");
            }

            // Only IP, TCP and this peer id: the library would resolve any other component, such as a name inside, before its first await.
            List<Multiaddress> tcp = [.. (await ResolveAsync(addrs).WaitAsync(token))
                .Where(resolved => resolved.Protocols is [IP4 or IP6, TCP, P2P] && resolved.GetPeerId() == peerId)
                .Distinct()
                .Take(MaxDialAddresses)];

            if (tcp.Count == 0)
            {
                throw new Libp2pException($"No TCP address to dial {peerId}");
            }

            // A dial cancelled before the library's first await would stay the peer's pending dial for good, so the token stops only this
            // wait; the dial itself ends within the library's connection timeout.
            Task<ISession> dial = DialAsync([.. tcp], CancellationToken.None);
            _ = dial.ContinueWith(static (completed, state) =>
            {
                // Disposal closes only the sessions it sees, so one a dial makes afterwards is closed here.
                if (completed.IsCompletedSuccessfully)
                {
                    if (Volatile.Read(ref ((IdentifyingPeer)state!)._disposed) == 1)
                    {
                        _ = completed.Result.DisconnectAsync();
                    }
                }
                // A caller that stopped waiting leaves the failure unobserved; observing it keeps it out of UnobservedTaskException.
                else _ = completed.Exception;
            }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await dial.WaitAsync(token);
        }

        ValueTask IAsyncDisposable.DisposeAsync()
        {
            Volatile.Write(ref _disposed, 1);
            return DisposeAsync();
        }

        // A name that does not resolve leaves the other addresses of the peer to dial.
        private async Task<List<Multiaddress>> ResolveAsync(Multiaddress[] addrs)
        {
            MultiaddrResolver resolver = new(new CountedDnsLookup(_dnsLookup, MaxDnsQueries));
            List<Multiaddress> resolved = [];
            foreach (Multiaddress addr in addrs)
            {
                try
                {
                    await foreach (Multiaddress address in resolver.Resolve(addr))
                    {
                        resolved.Add(address);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                }
            }

            return resolved;
        }
    }

    /// <summary>Refuses queries past <paramref name="maxQueries"/>, which ends a dnsaddr resolution whose records name each other.</summary>
    private sealed class CountedDnsLookup(IDnsLookup inner, int maxQueries) : IDnsLookup
    {
        private int _queries;

        public Task<IEnumerable<string>> QueryTxtAsync(string name) => Count() ? inner.QueryTxtAsync(name) : Refuse<string>(name);

        public Task<IEnumerable<IPAddress>> QueryAAsync(string name) => Count() ? inner.QueryAAsync(name) : Refuse<IPAddress>(name);

        public Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) => Count() ? inner.QueryAaaaAsync(name) : Refuse<IPAddress>(name);

        private bool Count() => Interlocked.Increment(ref _queries) <= maxQueries;

        private Task<IEnumerable<T>> Refuse<T>(string name) =>
            Task.FromException<IEnumerable<T>>(new Libp2pException($"Resolving {name} took more than {maxQueries} DNS queries"));
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
