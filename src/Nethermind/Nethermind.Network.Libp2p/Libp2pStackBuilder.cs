// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
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

        // The library push only dials (its notifier looks it up by type); a push the remote sends reaches the bounded listener, which comes first.
        List<ProtocolRef> apps = [Get<BoundedIdentifyProtocol>(), Get<BoundedIdentifyPushProtocol>(), Get<IdentifyPushProtocol>(), Get<PingProtocol>(), .. additionalProtocols];
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

        public async Task<IEnumerable<IPAddress>> QueryAAsync(string name) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetwork);

        public async Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) => await Dns.GetHostAddressesAsync(name, AddressFamily.InterNetworkV6);
    }

    /// <remarks>Dials through <see cref="ILocalPeer"/> reach the library only with resolved TCP addresses of one peer id: the library resolves
    /// names on the dialing thread with no bound on the queries, and one name that does not resolve fails the whole dial.</remarks>
    private sealed class IdentifyingPeer : LocalPeer, ILocalPeer
    {
        // A TXT record may name another dnsaddr, and the library dials every address of a peer at once.
        private const int MaxDnsQueries = 32;
        private static readonly TimeSpan DnsDeadline = TimeSpan.FromSeconds(10);
        private const int MaxDialAddresses = 16;

        private readonly IDnsLookup _dnsLookup;
        private readonly ILogger? _resolutionLogger;
        private int _disposed;

        public IdentifyingPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory,
            IDnsLookup dnsLookup)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory)
        {
            _dnsLookup = dnsLookup;
            _resolutionLogger = loggerFactory?.CreateLogger<Libp2pStackPeerFactory>();
            notifier.TrackChanges(this);
            Sessions.CollectionChanged += CloseAfterDisposal;
        }

        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            using CancellationTokenSource timeout = new(BoundedIdentifyProtocol.ReadTimeout);
            try
            {
                await session.DialAsync<BoundedIdentifyProtocol>(timeout.Token);
            }
            catch
            {
                await session.DisconnectAsync();
                throw;
            }
        }

        Task<ISession> ILocalPeer.DialAsync(Multiaddress addr, CancellationToken token) => DialTcpAsync(addr.GetPeerId(), [addr], token);

        Task<ISession> ILocalPeer.DialAsync(Multiaddress[] samePeerAddrs, CancellationToken token) =>
            DialTcpAsync(samePeerAddrs.Length == 0 ? null : samePeerAddrs[0].GetPeerId(), samePeerAddrs, token);

        Task<ISession> ILocalPeer.DialAsync(PeerId peerId, CancellationToken token) =>
            FindSession(peerId) is { } existing ? Task.FromResult<ISession>(existing)
            : DialTcpAsync(peerId, _peerStore?.GetPeerInfo(peerId).Addrs is { } addresses ? [.. addresses] : [], token);

        private Session? FindSession(PeerId peerId)
        {
            lock (Sessions)
            {
                foreach (Session session in Sessions)
                {
                    if (session.State.RemotePeerId == peerId) return session;
                }
            }
            return null;
        }

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

            foreach (Multiaddress addr in addrs)
            {
                if (addr.GetPeerId() != peerId)
                {
                    throw new Libp2pException($"A dial to {peerId} has addresses of another peer id");
                }
            }

            // Only IP, TCP and this peer id: the library would resolve any other component, such as a name inside, on the dialing thread,
            // and fails the whole dial on an address of another peer id, as a dnsaddr record can name.
            List<Multiaddress> tcp = [];
            List<(Multiaddress Address, Exception Error)> failures = [];
            foreach (Multiaddress resolved in await ResolveAsync(addrs, failures).WaitAsync(token))
            {
                if (resolved.Protocols is [IP4 or IP6, TCP, P2P] && resolved.GetPeerId() == peerId && !tcp.Contains(resolved))
                {
                    tcp.Add(resolved);
                    if (tcp.Count == MaxDialAddresses) break;
                }
            }

            if (tcp.Count == 0)
            {
                // Callers log only this exception, as their host may have no logger factory for the resolution log.
                throw failures.Count == 0
                    ? new Libp2pException($"No TCP address to dial {peerId}")
                    : new Libp2pException($"No TCP address to dial {peerId}: {string.Join("; ", failures.Select(static f => $"{f.Address}: {f.Error.Message}"))}",
                        new AggregateException(failures.Select(static f => f.Error)));
            }

            return await DialAsync([.. tcp], token);
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

        // A name that does not resolve leaves the other addresses of the peer to dial.
        private async Task<List<Multiaddress>> ResolveAsync(Multiaddress[] addrs, List<(Multiaddress Address, Exception Error)> failures)
        {
            using CountedDnsLookup lookup = new(_dnsLookup, MaxDnsQueries, DnsDeadline);
            MultiaddrResolver resolver = new(lookup);
            List<Multiaddress> resolved = [];
            foreach (Multiaddress addr in addrs)
            {
                int count = resolved.Count;
                try
                {
                    await foreach (Multiaddress address in resolver.Resolve(addr))
                    {
                        resolved.Add(address);
                    }
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    _resolutionLogger?.LogDebug(e, "Failed to resolve {Address}", addr);
                    failures.Add((addr, e));
                }

                // A failed address query answers no addresses, so these are the only causes left for a name that resolved to none.
                List<SocketException> queryFailures = lookup.TakeAddressFailures();
                if (resolved.Count == count)
                {
                    failures.AddRange(queryFailures.Select(e => (addr, (Exception)e)));
                }
            }

            return resolved;
        }
    }

    /// <summary>Refuses queries past <paramref name="maxQueries"/> or <paramref name="deadline"/>, which ends a dnsaddr resolution whose records
    /// name each other or whose server never answers, and answers a failed address query with no addresses so the other family still runs.</summary>
    private sealed class CountedDnsLookup(IDnsLookup inner, int maxQueries, TimeSpan deadline) : IDnsLookup, IDisposable
    {
        private readonly CancellationTokenSource _deadline = new(deadline);
        private readonly ConcurrentQueue<SocketException> _addressFailures = new();
        private int _queries;

        public Task<IEnumerable<string>> QueryTxtAsync(string name) => Count() ? Bounded(inner.QueryTxtAsync(name), name) : Refuse<string>(name);

        public Task<IEnumerable<IPAddress>> QueryAAsync(string name) => Count() ? OrNone(Bounded(inner.QueryAAsync(name), name)) : Refuse<IPAddress>(name);

        public Task<IEnumerable<IPAddress>> QueryAaaaAsync(string name) => Count() ? OrNone(Bounded(inner.QueryAaaaAsync(name), name)) : Refuse<IPAddress>(name);

        public void Dispose() => _deadline.Dispose();

        /// <summary>The address queries that failed since the last call, which answered no addresses instead.</summary>
        public List<SocketException> TakeAddressFailures()
        {
            List<SocketException> failures = [];
            while (_addressFailures.TryDequeue(out SocketException? failure))
            {
                failures.Add(failure);
            }

            return failures;
        }

        // The library's resolver takes no token, so the deadline stops the wait; the query itself is only observed.
        private async Task<IEnumerable<T>> Bounded<T>(Task<IEnumerable<T>> query, string name)
        {
            try
            {
                return await query.WaitAsync(_deadline.Token);
            }
            catch (OperationCanceledException) when (_deadline.IsCancellationRequested)
            {
                _ = query.ContinueWith(static abandoned => _ = abandoned.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                throw new Libp2pException($"Resolving {name} took longer than {deadline}");
            }
        }

        private async Task<IEnumerable<IPAddress>> OrNone(Task<IEnumerable<IPAddress>> query)
        {
            try
            {
                return await query;
            }
            catch (SocketException e)
            {
                _addressFailures.Enqueue(e);
                return [];
            }
        }

        private bool Count() => Interlocked.Increment(ref _queries) <= maxQueries && !_deadline.IsCancellationRequested;

        private Task<IEnumerable<T>> Refuse<T>(string name) =>
            Task.FromException<IEnumerable<T>>(new Libp2pException($"Resolving {name} took more than {maxQueries} DNS queries or {deadline}"));
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
