// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;

namespace Nethermind.Network.Libp2p;

/// <summary>The libp2p stack the node's hosts run on: TCP/Noise with Yamux or Mplex, optional QUIC, identify, ping and optional gossipsub.</summary>
/// <remarks>Uses component packages without the meta package's WebRTC, WebSocket and circuit-relay dependencies.</remarks>
public sealed class Libp2pStackBuilder(IServiceProvider? serviceProvider = null)
    : PeerFactoryBuilderBase<Libp2pStackBuilder, Libp2pStackPeerFactory>(serviceProvider)
{
    private bool _pubsub;
    private bool _quic;

    /// <summary>Adds the authenticated QUIC v1 transport, with its native stream multiplexing.</summary>
    public Libp2pStackBuilder WithQuic()
    {
        _quic = true;
        return this;
    }

    /// <summary>Adds gossipsub (v1.0 to v1.3) and floodsub to the protocols every session offers.</summary>
    public Libp2pStackBuilder WithPubsub()
    {
        _pubsub = true;
        return this;
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(IpTcpProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(NoiseProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(YamuxProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MplexProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(MultistreamProtocol))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicMethods, typeof(QuicProtocol))]
    protected override ProtocolRef[] BuildStack(IEnumerable<ProtocolRef> additionalProtocols)
    {
        ProtocolRef tcp = Get<IpTcpProtocol>();
        ProtocolRef[] appSelector = [Get<MultistreamProtocol>()];
        ProtocolRef[] multiplexers = [Get<YamuxProtocol>(), Get<MplexProtocol>()];
        Connect([tcp], [Get<MultistreamProtocol>()], [Get<NoiseProtocol>()], [Get<MultistreamProtocol>()], multiplexers, appSelector);

        List<ProtocolRef> apps = [Get<IdentifyProtocol>(), Get<IdentifyPushProtocol>(), Get<PingProtocol>(), .. additionalProtocols];
        if (_pubsub)
        {
            apps.AddRange([Get<GossipsubProtocolV13>(), Get<GossipsubProtocolV12>(), Get<GossipsubProtocolV11>(), Get<GossipsubProtocol>(), Get<FloodsubProtocol>()]);
        }

        // A typed array is one choice of protocols; a collection expression would bind to the params array and chain them instead.
        ProtocolRef[] choices = [.. apps];
        Connect(appSelector, choices);
        if (_quic)
        {
            ProtocolRef quic = Get<QuicProtocol>();
            Connect([quic], appSelector);
            return [tcp, quic];
        }
        return [tcp];
    }
}

/// <summary>Creates peers that run identify on every new session and push it when their listen addresses change.</summary>
/// <param name="dnsLookup">Answers the DNS queries that resolve names in dialed addresses; the operating system and DnsClient when null.</param>
public class Libp2pStackPeerFactory(IProtocolStackSettings protocolStackSettings, PeerStore peerStore, IdentifyNotifier identifyNotifier, ILoggerFactory? loggerFactory = null,
    IDnsLookup? dnsLookup = null)
    : PeerFactory(protocolStackSettings, peerStore, loggerFactory: loggerFactory)
{
    public override ILocalPeer Create(Identity? identity = null) =>
        new IdentifyingPeer(identity ?? new Identity(privateKey: null, KeyType.Secp256K1), PeerStore, protocolStackSettings, identifyNotifier, LoggerFactory,
            dnsLookup ?? new SystemDnsLookup());

    /// <summary>Answers A and AAAA queries through the operating system, which reads the hosts file, and TXT queries through DnsClient,
    /// which the operating system resolver cannot make.</summary>
    private sealed class SystemDnsLookup : IDnsLookup
    {
        private readonly DnsClientLookup _txt = new();

        public Task<IEnumerable<string>> QueryTxtAsync(string name) => _txt.QueryTxtAsync(name);

        public Task<IEnumerable<string>> QueryTxtAsync(string name, CancellationToken token) => _txt.QueryTxtAsync(name, token);

        public async Task<IEnumerable<IPAddress>> QueryAAsync(string name) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork);

        public async Task<IEnumerable<IPAddress>> QueryAAsync(string name, CancellationToken token) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork, token);

        public async Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetworkV6);

        public async Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name, CancellationToken token) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetworkV6, token);
    }

    private sealed class IdentifyingPeer : LocalPeer, ILocalPeer
    {
        private int _disposed;

        public IdentifyingPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory,
            IDnsLookup dnsLookup)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory, multiaddrResolver: new MultiaddrResolver(dnsLookup))
        {
            notifier.TrackChanges(this);
            Sessions.CollectionChanged += CloseAfterDisposal;
        }

        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            try
            {
                await session.DialAsync<IdentifyProtocol>(timeout.Token);
            }
            catch
            {
                await session.DisconnectAsync();
                throw;
            }
        }
        ValueTask IAsyncDisposable.DisposeAsync()
        {
            // Under the Sessions lock: a session added before this is in the library's disposal snapshot, one added after sees the flag.
            lock (Sessions)
            {
                Volatile.Write(ref _disposed, 1);
            }

            return DisposeAsync();
        }

        // Disposal closes only the sessions it sees, so one a dial completes afterwards is closed here, off the library's lock.
        private void CloseAfterDisposal(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems is not null && Volatile.Read(ref _disposed) == 1)
            {
                foreach (ISession session in e.NewItems)
                {
                    _ = Task.Run(session.DisconnectAsync);
                }
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
