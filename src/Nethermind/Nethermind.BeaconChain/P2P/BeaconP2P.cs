// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using ILogger = Nethermind.Logging.ILogger;
using ILoggerFactory = Microsoft.Extensions.Logging.ILoggerFactory;

namespace Nethermind.BeaconChain.P2P;

/// <summary>
/// The beacon chain libp2p host: TCP + noise + yamux with the eth2 req/resp protocols and
/// gossipsub registered, exposing typed request methods over dialed sessions and pubsub topics.
/// </summary>
/// <remarks>
/// The host identity is a secp256k1 key persisted in the store metadata column, so the peer ID is
/// stable across restarts (and reusable for the discv5 ENR in a later milestone). Gossipsub uses
/// the eth2 parameters: <c>StrictNoSign</c>, the eth2 message-id function
/// (<see cref="Eth2MessageId"/>), D=8/D_low=6/D_high=12/D_lazy=6, a 700 ms heartbeat, and a seen
/// TTL of 550 heartbeats. Note that the pinned libp2p preview always signs published messages,
/// which StrictNoSign peers reject — receiving gossip works, but publishing needs a library fix.
/// </remarks>
public sealed class BeaconP2P : IAsyncDisposable
{
    private const string IdentityMetadataKey = "p2pIdentityKey";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly IBeaconChainConfig _config;
    private readonly LocalMetadataSource _metadataSource;
    private readonly IBeaconChainStatusSource _statusSource;
    private readonly BeaconChainStore _store;
    private readonly ILogger _logger;
    private readonly ServiceProvider _serviceProvider;

    // What the libp2p layer learns about each session that the session object itself does not tell:
    // which side dialed, and the identify agent string. A slot opens the moment the library adds the
    // session, completes once identify and the agent probe are done (see BeaconLocalPeer), and is
    // cancelled when the library drops the session. It is a slot and not a value because a dial
    // returns as soon as the library's identify completes, while the probe is still in flight.
    private readonly ConcurrentDictionary<ISession, TaskCompletionSource<SessionInfo>> _sessionInfo = new();

    private LocalPeer? _localPeer;
    private PubsubRouter? _router;

    /// <summary>The per-session facts <see cref="PeerManager"/> cannot read off an <see cref="ISession"/>.
    /// <paramref name="AgentVersion"/> is <c>null</c> only when the peer did not answer the identify
    /// probe (see <see cref="IdentifyAgentVersionProbe"/>), never as a stand-in for "not wired".</summary>
    public readonly record struct SessionInfo(PeerDirection Direction, string? AgentVersion);

    /// <summary>Raised once a session is fully established (identify done) in either direction. This is
    /// the only way a session the remote side opened, which no local dial will ever return, reaches the
    /// peer manager's admission gate.</summary>
    public event Action<ISession, SessionInfo>? SessionEstablished;

    public BeaconP2P(
        IBeaconChainConfig config,
        BeaconChainSpec spec,
        BeaconChainStore store,
        IBeaconChainStatusSource statusSource,
        LocalMetadataSource metadataSource,
        DataColumnSidecarPool dataColumnSidecarPool,
        ExecutionPayloadEnvelopePool executionPayloadEnvelopePool,
        ILogManager logManager)
    {
        _config = config;
        _store = store;
        _statusSource = statusSource;
        _metadataSource = metadataSource;
        _logger = logManager.GetClassLogger<BeaconP2P>();

        _serviceProvider = new ServiceCollection()
            .AddSingleton<PeerStore>()
            .AddSingleton(new StatusProtocolV1(statusSource))
            .AddSingleton(new StatusProtocolV2(statusSource))
            .AddSingleton(new GoodbyeProtocol())
            .AddSingleton(new Eth2PingProtocol(metadataSource))
            .AddSingleton(new MetaDataProtocolV3(metadataSource))
            .AddSingleton(new BeaconBlocksByRangeProtocolV2(spec, store))
            .AddSingleton(new BeaconBlocksByRootProtocolV2(spec, store))
            .AddSingleton(new DataColumnSidecarsByRangeProtocol(spec, dataColumnSidecarPool))
            .AddSingleton(new DataColumnSidecarsByRootProtocol(spec, dataColumnSidecarPool))
            .AddSingleton(new ExecutionPayloadEnvelopesByRangeProtocol(spec, executionPayloadEnvelopePool))
            .AddSingleton(new ExecutionPayloadEnvelopesByRootProtocol(spec, executionPayloadEnvelopePool))
            .AddLibp2p(builder => builder
                .WithPubsub()
                .AddAppLayerProtocol<StatusProtocolV1>()
                .AddAppLayerProtocol<StatusProtocolV2>()
                .AddAppLayerProtocol<GoodbyeProtocol>()
                .AddAppLayerProtocol<Eth2PingProtocol>()
                .AddAppLayerProtocol<MetaDataProtocolV3>()
                .AddAppLayerProtocol<BeaconBlocksByRangeProtocolV2>()
                .AddAppLayerProtocol<BeaconBlocksByRootProtocolV2>()
                .AddAppLayerProtocol<DataColumnSidecarsByRangeProtocol>()
                .AddAppLayerProtocol<DataColumnSidecarsByRootProtocol>()
                .AddAppLayerProtocol<ExecutionPayloadEnvelopesByRangeProtocol>()
                .AddAppLayerProtocol<ExecutionPayloadEnvelopesByRootProtocol>()
                .AddAppLayerProtocol<IdentifyAgentVersionProbe>())
            // One identify instance: the library's own stack slot and the probe's listen fallback
            // both resolve to it, so an inbound identify request is answered the same way whichever
            // of the two same-id protocols multistream picks.
            .AddSingleton<IdentifyProtocol>()
            .AddSingleton<IdentifyAgentVersionProbe>()
            // The library's peer class is internal; this one does the same identify handshake and also
            // records the session direction and agent string (see BeaconLocalPeer).
            .AddSingleton<Libp2pPeerFactory>(sp =>
            {
                IProtocolStackSettings settings = sp.GetRequiredService<IProtocolStackSettings>();
                PeerStore peerStore = sp.GetRequiredService<PeerStore>();
                IdentifyNotifier notifier = sp.GetRequiredService<IdentifyNotifier>();
                ILoggerFactory? loggerFactory = sp.GetService<ILoggerFactory>();
                return new BeaconPeerFactory(settings, peerStore, notifier, loggerFactory,
                    identity => new BeaconLocalPeer(identity, peerStore, settings, notifier, loggerFactory, this));
            })
            .AddSingleton(new IdentifyProtocolSettings
            {
                ProtocolVersion = "eth2/1.0.0",
                AgentVersion = $"nethermind/{ProductInfo.Version}",
            })
            // The eth2 gossipsub parameters (consensus-specs p2p-interface "The gossip domain: gossipsub").
            .AddSingleton(new PubsubSettings
            {
                DefaultSignaturePolicy = PubsubSettings.SignaturePolicy.StrictNoSign,
                GetMessageId = static message => new MessageId(Eth2MessageId.Compute(message.Topic, message.Data.Span)),
                Degree = 8, // D
                LowestDegree = 6, // D_low
                HighestDegree = 12, // D_high
                LazyDegree = 6, // D_lazy
                HeartbeatInterval = 700, // heartbeat_interval: 0.7 s
                FanoutTtl = 60_000, // fanout_ttl: 60 s
                mcache_len = 6,
                mcache_gossip = 3,
                MessageCacheTtl = 550 * 700, // seen_ttl: 550 heartbeats
            })
            // Cap libp2p-internal logs at Debug: most mainnet dials fail and the library logs
            // each failed upgrade as a Warning with a full stack trace, drowning the useful output.
            .AddSingleton<ILoggerFactory>(new NethermindLoggerFactory(logManager, lowerLogLevel: true, maxLogLevel: Microsoft.Extensions.Logging.LogLevel.Debug))
            .BuildServiceProvider();
    }

    public PeerId? LocalPeerId => _localPeer?.Identity.PeerId;

    public IReadOnlyList<Multiaddress> ListenAddresses => _localPeer is null ? [] : [.. _localPeer.ListenAddresses];

    /// <summary>Starts listening and the pubsub router.</summary>
    /// <param name="token">Must stay uncancelled for the host lifetime: the pubsub heartbeat and reconnect loops are bound to it.</param>
    public async Task StartAsync(CancellationToken token)
    {
        _localPeer = (LocalPeer)_serviceProvider.GetRequiredService<IPeerFactory>().Create(LoadOrCreateIdentity());
        _localPeer.OnConnected += OnSessionConnected;
        _localPeer.Sessions.CollectionChanged += OnSessionsChanged;
        await _localPeer.StartListenAsync([$"/ip4/0.0.0.0/tcp/{_config.P2PPort}"], token);
        _router = _serviceProvider.GetRequiredService<PubsubRouter>();
        await _router.StartAsync(_localPeer, token);
        if (_logger.IsInfo) _logger.Info($"Beacon chain P2P listening on port {_config.P2PPort} as {LocalPeerId}");
    }

    /// <summary>Gets (and subscribes) the pubsub topic; available after <see cref="StartAsync"/>.</summary>
    public ITopic GetTopic(string topicId) =>
        (_router ?? throw new InvalidOperationException($"{nameof(BeaconP2P)} is not started")).GetTopic(topicId);

    /// <summary>Feeds known peer addresses to the peer store so the pubsub router connects to them.</summary>
    public void Discover(Multiaddress[] addresses) => _serviceProvider.GetRequiredService<PeerStore>().Discover(addresses);

    /// <summary>The direction and agent string recorded for a live session, waiting for the identify
    /// handshake and agent probe when the session is that fresh. Throws once the library has dropped
    /// the session, including while waiting.</summary>
    public async Task<SessionInfo> GetSessionInfoAsync(ISession session, CancellationToken token)
    {
        if (!_sessionInfo.TryGetValue(session, out TaskCompletionSource<SessionInfo>? slot))
        {
            throw new InvalidOperationException("Session is no longer tracked by the libp2p layer");
        }

        try
        {
            return await slot.Task.WaitAsync(token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new InvalidOperationException("Session closed before identify completed");
        }
    }

    /// <summary>The live session whose handshake established <paramref name="peerId"/>, whichever side opened it.</summary>
    public bool TryGetEstablishedSession(PeerId peerId, [NotNullWhen(true)] out ISession? session)
    {
        LocalPeer localPeer = _localPeer ?? throw new InvalidOperationException($"{nameof(BeaconP2P)} is not started");
        // Snapshot: the collection can change concurrently as connections come and go.
        LocalPeer.Session[] sessions = [.. localPeer.Sessions];
        foreach (LocalPeer.Session candidate in sessions)
        {
            if (candidate.State.RemotePublicKey is not null && peerId.Equals(candidate.State.RemotePeerId))
            {
                session = candidate;
                return true;
            }
        }

        session = null;
        return false;
    }

    /// <summary>The peer id the session's handshake actually established, as opposed to whatever the dial address claimed.</summary>
    public static PeerId? RemotePeerIdOf(ISession session) => (session as LocalPeer.Session)?.State.RemotePeerId;

    /// <summary>Internal so a test can give one node a distinguishable agent string before it connects.</summary>
    internal IdentifyProtocolSettings IdentifySettingsForTest => _serviceProvider.GetRequiredService<IdentifyProtocolSettings>();

    /// <summary>Internal so a test can observe a refused inbound session being torn down, not just never admitted.</summary>
    internal int SessionCountForTest => _localPeer?.Sessions.Count ?? 0;

    /// <summary>Dials the peer, or returns the existing session when one is already established (for example inbound).</summary>
    /// <remarks>
    /// The existing-session check mirrors newer dotnet-libp2p behavior; in the pinned preview a
    /// second dial to an already-connected peer fails the upgrade with a session-exists error
    /// instead of reusing the connection. The same check runs again after a failed dial: when both
    /// sides dial at once ours loses the upgrade to the session the remote opened, and that session
    /// is the connection to hand back, not a failure. The caller's own cancellation is neither: it
    /// propagates even when such a session exists.
    /// </remarks>
    public async Task<ISession> DialPeerAsync(Multiaddress address, CancellationToken token)
    {
        LocalPeer localPeer = _localPeer ?? throw new InvalidOperationException($"{nameof(BeaconP2P)} is not started");
        PeerId? remotePeerId = address.GetPeerId();
        if (remotePeerId is not null && TryGetEstablishedSession(remotePeerId, out ISession? existing))
        {
            return existing;
        }

        try
        {
            return await localPeer.DialAsync(address, token);
        }
        catch (Exception e) when ((e is not OperationCanceledException || !token.IsCancellationRequested)
                                  && remotePeerId is not null && TryGetEstablishedSession(remotePeerId, out ISession? raced))
        {
            return raced;
        }
    }

    /// <summary>Exchanges <c>status</c> with the peer, preferring v2 and falling back to v1 (with <c>earliest_available_slot</c> of 0).</summary>
    public async Task<StatusMessageV2> RequestStatusAsync(ISession session, CancellationToken token)
    {
        try
        {
            using CancellationTokenSource cts = Timeout(token);
            return await session.DialAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(_statusSource.CurrentStatus, cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Status v2 with {session.RemoteAddress} failed ({e.Message}), falling back to v1");
            using CancellationTokenSource cts = Timeout(token);
            return await session.DialAsync<StatusProtocolV1, StatusMessageV2, StatusMessageV2>(_statusSource.CurrentStatus, cts.Token);
        }
    }

    public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ISession session, ulong startSlot, ulong count, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(count));
        return await session.DialAsync<BeaconBlocksByRangeProtocolV2, BeaconBlocksByRangeRequest, IReadOnlyList<ForkedSignedBeaconBlock>>(
            new BeaconBlocksByRangeRequest { StartSlot = startSlot, Count = count, Step = 1 }, cts.Token);
    }

    public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(ISession session, Hash256[] roots, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(roots.Length));
        return await session.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(roots, cts.Token);
    }

    public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ISession session, ulong startSlot, ulong count, ulong[] columns, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(count));
        return await session.DialAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsByRangeRequest, IReadOnlyList<DataColumnSidecar>>(
            new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns }, cts.Token);
    }

    public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(ISession session, DataColumnsByRootIdentifier[] identifiers, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(identifiers.Length));
        return await session.DialAsync<DataColumnSidecarsByRootProtocol, DataColumnsByRootIdentifier[], IReadOnlyList<DataColumnSidecar>>(identifiers, cts.Token);
    }

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ISession session, ulong startSlot, ulong count, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(count));
        return await session.DialAsync<ExecutionPayloadEnvelopesByRangeProtocol, ExecutionPayloadEnvelopesByRangeRequest, IReadOnlyList<SignedExecutionPayloadEnvelope>>(
            new ExecutionPayloadEnvelopesByRangeRequest { StartSlot = startSlot, Count = count }, cts.Token);
    }

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(ISession session, Hash256[] roots, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(roots.Length));
        return await session.DialAsync<ExecutionPayloadEnvelopesByRootProtocol, Hash256[], IReadOnlyList<SignedExecutionPayloadEnvelope>>(roots, cts.Token);
    }

    /// <summary>Pings the peer with our metadata sequence number; returns theirs.</summary>
    public async Task<ulong> PingAsync(ISession session, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token);
        return await session.DialAsync<Eth2PingProtocol, ulong, ulong>(_metadataSource.Current.SeqNumber, cts.Token);
    }

    public async Task<MetaDataV3> RequestMetaDataAsync(ISession session, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token);
        return await session.DialAsync<MetaDataProtocolV3, ulong, MetaDataV3>(0, cts.Token);
    }

    /// <summary>Sends <c>goodbye</c> best-effort; failures are ignored since the peer is being dropped anyway.</summary>
    public async Task GoodbyeAsync(ISession session, ulong reason, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token);
        try
        {
            await session.DialAsync<GoodbyeProtocol, ulong, ulong>(reason, cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Goodbye to {session.RemoteAddress} failed: {e.Message}");
        }
    }

    private static CancellationTokenSource Timeout(CancellationToken token, TimeSpan? timeout = null)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout ?? RequestTimeout);
        return cts;
    }

    private Identity LoadOrCreateIdentity()
    {
        byte[]? privateKey = _store.GetMetadata(IdentityMetadataKey);
        if (privateKey is not null)
        {
            return new Identity(privateKey, KeyType.Secp256K1);
        }

        Identity identity = new(privateKey: null, KeyType.Secp256K1);
        _store.PutMetadata(IdentityMetadataKey, identity.PrivateKey!.Data.ToByteArray());
        if (_logger.IsInfo) _logger.Info($"Generated new beacon chain P2P identity {identity.PeerId}");
        return identity;
    }

    private Task OnSessionConnected(ISession session)
    {
        // Runs on the library's continuation after ConnectedTo completed, so the slot is filled by now;
        // a throwing subscriber must not cost the session.
        try
        {
            if (_sessionInfo.TryGetValue(session, out TaskCompletionSource<SessionInfo>? slot) && slot.Task.IsCompletedSuccessfully)
            {
                SessionEstablished?.Invoke(session, slot.Task.Result);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsError) _logger.Error($"Session-established handler failed for {session.RemoteAddress}", e);
        }

        return Task.CompletedTask;
    }

    private void OnSessionsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Raised inside the library's Sessions lock, before ConnectedTo starts and before any dial can
        // observe the session: every session a caller can see has a slot, so a missing one means dropped.
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewItems is not null:
                foreach (object? added in e.NewItems)
                {
                    if (added is ISession session)
                    {
                        _sessionInfo.TryAdd(session, new TaskCompletionSource<SessionInfo>(TaskCreationOptions.RunContinuationsAsynchronously));
                    }
                }

                break;
            case NotifyCollectionChangedAction.Remove when e.OldItems is not null:
                foreach (object? removed in e.OldItems)
                {
                    if (removed is ISession session && _sessionInfo.TryRemove(session, out TaskCompletionSource<SessionInfo>? slot))
                    {
                        slot.TrySetCanceled();
                    }
                }

                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (KeyValuePair<ISession, TaskCompletionSource<SessionInfo>> slot in _sessionInfo)
                {
                    slot.Value.TrySetCanceled();
                }

                _sessionInfo.Clear();
                break;
        }
    }

    /// <summary>Best effort: an unanswered probe leaves the agent string unknown, it never costs the session.
    /// Bounded tighter than a request because every admission waits on it.</summary>
    private async Task<string?> ProbeAgentVersionAsync(ISession session)
    {
        try
        {
            using CancellationTokenSource cts = Timeout(CancellationToken.None, IdentifyAgentVersionProbe.ReadTimeout);
            return await session.DialAsync<IdentifyAgentVersionProbe, ulong, string?>(0, cts.Token);
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Identify agent-version probe of {session.RemoteAddress} failed: {e.Message}");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_localPeer is not null)
        {
            await _localPeer.DisposeAsync();
        }

        await _serviceProvider.DisposeAsync();
    }

    /// <summary>
    /// Stands in for the library's own (internal) peer class, whose whole job is the identify handshake
    /// on every new session. This one also records which side dialed and the peer's agent string: the
    /// two facts a session does not tell after the fact, which is why inbound sessions used to be
    /// invisible to <see cref="PeerManager"/> and every directory entry read as Outbound with no client string.
    /// </summary>
    private sealed class BeaconLocalPeer : LocalPeer
    {
        private readonly BeaconP2P _owner;

        public BeaconLocalPeer(Identity identity, PeerStore peerStore, IProtocolStackSettings settings, IdentifyNotifier notifier, ILoggerFactory? loggerFactory, BeaconP2P owner)
            : base(identity, peerStore, settings, loggerFactory: loggerFactory)
        {
            _owner = owner;
            notifier.TrackChanges(this);
        }

        protected override async Task ConnectedTo(ISession session, bool isDialer)
        {
            // The library's identify dial first: it verifies the remote identity and fills the peer store,
            // and a failure here disconnects the session, exactly as the library's own peer class behaves.
            await session.DialAsync<IdentifyProtocol>();
            string? agentVersion = await _owner.ProbeAgentVersionAsync(session);
            if (_owner._sessionInfo.TryGetValue(session, out TaskCompletionSource<SessionInfo>? slot))
            {
                slot.TrySetResult(new SessionInfo(isDialer ? PeerDirection.Outbound : PeerDirection.Inbound, agentVersion));
            }
        }
    }

    /// <summary>Only the creation delegate is captured: the stack settings and peer store go to the base
    /// class alone, which is what lets this stay a primary constructor without double-capturing them.</summary>
    private sealed class BeaconPeerFactory(IProtocolStackSettings settings, PeerStore peerStore, IdentifyNotifier notifier, ILoggerFactory? loggerFactory, Func<Identity, ILocalPeer> create)
        : Libp2pPeerFactory(settings, peerStore, notifier, loggerFactory: loggerFactory)
    {
        public override ILocalPeer Create(Identity? identity = null) => create(identity ?? new Identity(privateKey: null, KeyType.Secp256K1));
    }
}
