// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Multiformats.Address.Net;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Sync;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Libp2p.Core;
using Nethermind.Libp2p.Core.Discovery;
using Nethermind.Libp2p.Core.Exceptions;
using Nethermind.Libp2p.Core.Dto;
using Nethermind.Libp2p.Protocols;
using Nethermind.Libp2p.Protocols.Pubsub;
using Nethermind.Logging;
using Nethermind.Logging.Microsoft;
using Nethermind.Network.Libp2p;
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
/// TTL of two epochs (p2p-interface.md, gossipsub parameters).
/// </remarks>
public sealed class BeaconP2P : IAsyncDisposable
{
    private const string IdentityMetadataKey = "p2pIdentityKey";

    private readonly IBeaconChainConfig _config;
    private readonly LocalMetadataSource _metadataSource;
    private readonly IBeaconChainStatusSource _statusSource;
    private readonly BeaconChainStore _store;
    private readonly ILogger _logger;
    private readonly ServiceProvider _serviceProvider;
    private readonly GossipMessageValidator? _messageValidator;
    // Lazy because the pool's implementation is built from this host.
    private readonly Lazy<IBeaconSyncPeerPool>? _peerPool;

    // What the libp2p layer learns about each session that the session object itself does not tell:
    // which side dialed, and the identify agent string. A slot opens the moment the library adds the
    // session, completes once identify is done (see BeaconLocalPeer), and is cancelled when the
    // library drops the session. It is a slot and not a value because a dial returns as soon as the
    // identify dial completes, before the slot is filled.
    private readonly ConcurrentDictionary<ISession, TaskCompletionSource<SessionInfo>> _sessionInfo = new();

    // Cancelled when the library drops the session, so a request on it ends then instead of at its timeout.
    private readonly ConcurrentDictionary<ISession, SessionLifetime> _sessionClosed = new();
    private readonly ConcurrentDictionary<SessionWatch, byte> _sessionWatches = new();
    private int _identifyTimeouts;
    private int _disposed;

    private LocalPeer? _localPeer;
    private CancellationTokenSource? _startCts;
    private PubsubRouter? _router;
    private GossipTopicSubscriptions? _gossipSubscriptions;
    private DeferredGossipValidation? _deferredValidation;

    /// <summary>The per-session facts <see cref="PeerManager"/> cannot read off an <see cref="ISession"/>.
    /// <paramref name="AgentVersion"/> is <c>null</c> only when the peer's identify answer carries none
    /// (see <see cref="IdentifyAgentVersionProbe"/>), never as a stand-in for "not wired".</summary>
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
        ILogManager logManager,
        GossipMessageValidator? messageValidator = null,
        SlotClock? clock = null,
        Lazy<IBeaconSyncPeerPool>? peerPool = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(config.GossipMaxPendingValidations, 1, nameof(IBeaconChainConfig.GossipMaxPendingValidations));
        ArgumentOutOfRangeException.ThrowIfLessThan(config.GossipMaxPendingValidationBytes, 1, nameof(IBeaconChainConfig.GossipMaxPendingValidationBytes));
        _config = config;
        _peerPool = peerPool;
        _messageValidator = messageValidator;
        _store = store;
        _statusSource = statusSource;
        _metadataSource = metadataSource;
        _logger = logManager.GetClassLogger<BeaconP2P>();

        _serviceProvider = new ServiceCollection()
            .AddSingleton<PeerStore>()
            .AddSingleton(new StatusProtocolV1(statusSource) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new StatusProtocolV2(statusSource) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new GoodbyeProtocol { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new Eth2PingProtocol(metadataSource) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new MetaDataProtocolV3(metadataSource) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new BeaconBlocksByRangeProtocolV2(spec, store) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new BeaconBlocksByRootProtocolV2(spec, store) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new DataColumnSidecarsByRangeProtocol(spec, dataColumnSidecarPool, store, clock) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new DataColumnSidecarsByRootProtocol(spec, dataColumnSidecarPool) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new ExecutionPayloadEnvelopesByRangeProtocol(spec, executionPayloadEnvelopePool) { RequestViolationSink = ReportRequestViolation })
            .AddSingleton(new ExecutionPayloadEnvelopesByRootProtocol(spec, executionPayloadEnvelopePool) { RequestViolationSink = ReportRequestViolation })
            .AddLibp2p(builder => builder
                .WithPubsub()
                .AddProtocol<StatusProtocolV1>()
                .AddProtocol<StatusProtocolV2>()
                .AddProtocol<GoodbyeProtocol>()
                .AddProtocol<Eth2PingProtocol>()
                .AddProtocol<MetaDataProtocolV3>()
                .AddProtocol<BeaconBlocksByRangeProtocolV2>()
                .AddProtocol<BeaconBlocksByRootProtocolV2>()
                .AddProtocol<DataColumnSidecarsByRangeProtocol>()
                .AddProtocol<DataColumnSidecarsByRootProtocol>()
                .AddProtocol<ExecutionPayloadEnvelopesByRangeProtocol>()
                .AddProtocol<ExecutionPayloadEnvelopesByRootProtocol>()
                .AddProtocol<IdentifyAgentVersionProbe>())
            // One identify instance: the library's own stack slot and the probe's listen fallback
            // both resolve to it, so an inbound identify request is answered the same way whichever
            // of the two same-id protocols multistream picks.
            .AddSingleton(sp => new IdentifyProtocol(new ProbeHidingStackSettings(sp.GetRequiredService<IProtocolStackSettings>()),
                sp.GetRequiredService<IdentifyProtocolSettings>(), sp.GetRequiredService<PeerStore>(), sp.GetService<ILoggerFactory>()))
            .AddSingleton(sp => new IdentifyPushProtocol(new ProbeHidingStackSettings(sp.GetRequiredService<IProtocolStackSettings>()),
                sp.GetRequiredService<IdentifyProtocolSettings>(), sp.GetRequiredService<PeerStore>(), sp.GetService<ILoggerFactory>()))
            .AddSingleton<IdentifyAgentVersionProbe>()
            // The library's peer class is internal; this one does the same identify handshake and also
            // records the session direction and agent string (see BeaconLocalPeer).
            .AddSingleton<Libp2pStackPeerFactory>(sp =>
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
                AgentVersion = ClientAgentVersion,
            })
            // The eth2 gossipsub parameters (consensus-specs p2p-interface "The gossip domain: gossipsub").
            .AddSingleton(GossipScoring.Configure(new PubsubSettings
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
                MessageCacheTtl = checked((int)(spec.SecondsPerSlot * 1000 * spec.SlotsPerEpoch * 2)), // seen_ttl: two epochs, in ms
                MaxSeenMessageIds = MaxSeenMessageIds(spec),
                // phase0 p2p "Gossipsub size limits": an encoded RPC, an IWANT answer included, may reach max_message_size().
                MaxRpcBytes = Eth2MessageId.MaxMessageSize,
                MaxIwantResponseBytes = Eth2MessageId.MaxMessageSize,
                // The router would redial closed gossip peers past PeerManager's bans, band and backoff; PeerManager owns redials.
                ReconnectionAttempts = 0,
                // fulu/das-core.md "Reconstruction and cross-seeding": this node publishes only reconstructed columns, which go to the topic mesh neighbors.
                FloodPublish = false,
                // The sum of DeferredGossipValidation's bounds, which reserve every message they defer, so the router dispatches each of them.
                MaxPendingValidationMessages = PendingValidationMessagesBackstop(config),
                MaxPendingValidationBytes = PendingValidationBytesBackstop(config),
                // gossipsub v1.2 IDONTWANT: announce each accepted or published message of at least 1 KiB to the v1.2 mesh peers, and keep a
                // peer's announcement for three heartbeats.
                IdontwantMessageThreshold = 1024,
                IdontwantTtlHeartbeats = 3,
                MaxIdontwantLength = IdontwantIdsPerControl,
                MaxIdontwantMessages = MaxIdontwantControlsPerHeartbeat,
            }, ScheduledTopics(spec), spec))
            .AddSingleton(CreateLibp2pLoggerFactory(logManager))
            .BuildServiceProvider();
    }

    /// <summary>The router's own bound on messages awaiting a verdict: the node's bound and the vote queue, each reserved by DeferredGossipValidation, twice over.</summary>
    /// <remarks>The margin covers a message the router still holds for a moment after the node released its reservation.</remarks>
    internal static int PendingValidationMessagesBackstop(IBeaconChainConfig config) =>
        checked(2 * (config.GossipMaxPendingValidations + BeaconSyncOrchestrator.VoteQueueCapacity));

    /// <summary>The router's own bound on bytes awaiting a verdict: the node's bound and a full vote queue of the largest votes, twice over.</summary>
    internal static int PendingValidationBytesBackstop(IBeaconChainConfig config) =>
        checked(2 * (config.GossipMaxPendingValidationBytes + BeaconSyncOrchestrator.VoteQueueCapacity * DeferredGossipValidation.MaxVoteMessageBytes));

    /// <summary>The most message ids in one IDONTWANT control entry.</summary>
    internal const int IdontwantIdsPerControl = 10;

    /// <summary>The most IDONTWANT control entries sent to, or kept from, one peer per heartbeat.</summary>
    /// <remarks>
    /// Room for every large message of two blocks in one heartbeat, a column per subnet plus the block and its envelope, each in its own
    /// entry: the router announces each message as its verdict is given or as it is published. Past it an announcement is neither sent nor
    /// kept, which only costs a duplicate send, so a peer can make this node withhold at most
    /// <c>MaxIdontwantControlsPerHeartbeat * IdontwantIdsPerControl * 3</c> message ids.
    /// </remarks>
    internal const int MaxIdontwantControlsPerHeartbeat = 2 * ((int)Eip7594DasConstants.DataColumnSidecarSubnetCount + 2);

    /// <summary>The seen and limbo cache capacity, sized for the honest traffic of the subscribed topics over the two-epoch seen_ttl.</summary>
    /// <remarks>Per slot: the target aggregators of every committee, one vote per PTC member, one column per subnet and one block, envelope and slashing.
    /// Aggregator selection is probabilistic and invalid traffic is unbounded, so this is an estimate: past it the library evicts the oldest id early
    /// and a late duplicate is validated again (phase0 p2p-interface.md: clients SHOULD bound their queues).</remarks>
    internal static int MaxSeenMessageIds(BeaconChainSpec spec) =>
        checked((int)((Presets.MaxCommitteesPerSlot * Presets.TargetAggregatorsPerCommittee + Presets.PtcSize + Eip7594DasConstants.DataColumnSidecarSubnetCount
            + (ulong)(GossipTopics.SubscribedTopicNames.Length + GossipTopics.GloasTopicNames.Length)) * spec.SlotsPerEpoch * 2));

    /// <summary>Every topic this node can subscribe, of every scheduled fork digest.</summary>
    /// <remarks>A topic missing from the score table gets the library's default delivery score, which prunes honest peers of a sparse topic;
    /// <see cref="GossipScoring"/> gives these topics their parameters before the router starts.</remarks>
    internal static IEnumerable<string> ScheduledTopics(BeaconChainSpec spec)
    {
        string[] names =
        [
            .. GossipTopics.SubscribedTopicNames,
            .. GossipTopics.GloasTopicNames,
            .. Enumerable.Range(0, (int)Eip7594DasConstants.DataColumnSidecarSubnetCount).Select(static subnet => GossipTopics.DataColumnSidecarTopicName((ulong)subnet)),
        ];
        foreach (ulong epoch in GossipTopics.DigestRotationEpochs(spec, 0).Prepend(0UL))
        {
            byte[] digest = ForkDigest.Compute(spec, epoch);
            foreach (string name in names)
            {
                yield return GossipTopics.Topic(digest, name);
            }
        }
    }

    /// <summary>The logger factory for libp2p's own categories: nothing it logs reaches our log above Trace.</summary>
    /// <remarks>Most mainnet dials fail and the library logs each failed upgrade as an error whose text holds the whole stack trace, which would drown the Debug output.</remarks>
    internal static ILoggerFactory CreateLibp2pLoggerFactory(ILogManager logManager) =>
        new NethermindLoggerFactory(logManager, lowerLogLevel: true, maxLogLevel: Microsoft.Extensions.Logging.LogLevel.Trace);

    /// <summary>Records a protocol violation by an inbound requester against the peer's failure count, when the peer is connected.</summary>
    /// <remarks>The peer manager finds the peer by id; any other pool can only offer the peers selection would hand out.</remarks>
    internal void ReportRequestViolation(PeerId peerId, string detail)
    {
        if (_peerPool?.Value is PeerManager manager)
        {
            manager.TryReportInboundViolation(peerId, detail);
            return;
        }

        string peerSuffix = $"/p2p/{peerId}";
        foreach (IBeaconSyncPeer peer in _peerPool?.Value.GetBestPeers(0) ?? [])
        {
            if (peer.Id.EndsWith(peerSuffix, StringComparison.Ordinal))
            {
                peer.ReportFailure(PeerFailureReason.ProtocolViolation, detail);
                return;
            }
        }
    }

    /// <summary>The client string this node advertises over libp2p identify and reports from <c>/eth/v1/node/version</c>.</summary>
    internal static string ClientAgentVersion => ProductInfo.ClientId;

    public PeerId? LocalPeerId => _localPeer?.Identity.PeerId;

    public IReadOnlyList<Multiaddress> ListenAddresses => _localPeer is null ? [] : [.. _localPeer.ListenAddresses];

    /// <summary>Starts listening and the pubsub router.</summary>
    /// <param name="token">Must stay uncancelled for the host lifetime: the pubsub heartbeat and reconnect loops are bound to it.</param>
    public async Task StartAsync(CancellationToken token)
    {
        _startCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            _localPeer = (LocalPeer)_serviceProvider.GetRequiredService<IPeerFactory>().Create(LoadOrCreateIdentity());
            _localPeer.OnConnected += OnSessionConnected;
            _localPeer.Sessions.CollectionChanged += OnSessionsChanged;
            await _localPeer.StartListenAsync([$"/ip4/0.0.0.0/tcp/{_config.P2PPort}"], _startCts.Token);
            token.ThrowIfCancellationRequested();
            IPEndPoint? listenEndpoint = _localPeer.ListenAddresses.Count == 1 ? _localPeer.ListenAddresses[0].ToEndPoint() : null;
            // The library swallows a failed bind and reports no listen address instead.
            if (listenEndpoint is null || listenEndpoint.Port == 0)
            {
                throw new InvalidOperationException($"Beacon chain P2P failed to bind TCP port {_config.P2PPort}; check whether the port is already in use");
            }

            _router = _serviceProvider.GetRequiredService<PubsubRouter>();
            if (_messageValidator is not null)
            {
                PubsubSettings settings = _serviceProvider.GetRequiredService<PubsubSettings>();
                _deferredValidation = new DeferredGossipValidation(_router, _messageValidator, _config.GossipMaxPendingValidations, _config.GossipMaxPendingValidationBytes,
                    BeaconSyncOrchestrator.VoteQueueCapacity, settings.PendingValidationTimeout, _logger, _startCts.Token);
                _gossipSubscriptions = new GossipTopicSubscriptions(_router, _deferredValidation.Verify);
                _router.VerifyMessage = _gossipSubscriptions.Verify;
                _router.OnDeferredMessage = _deferredValidation.ValidateAsync;
            }

            await _router.StartAsync(_localPeer, _startCts.Token);
            if (_logger.IsInfo) _logger.Info($"Beacon chain P2P listening on port {listenEndpoint.Port} as {LocalPeerId}");
        }
        catch
        {
            await _startCts.CancelAsync();
            _startCts.Dispose();
            _startCts = null;
            if (_localPeer is not null)
            {
                await _localPeer.DisposeAsync();
                _localPeer = null;
            }

            throw;
        }
    }

    /// <summary>Gets (and subscribes) the pubsub topic; available after <see cref="StartAsync"/>.</summary>
    public ITopic GetTopic(string topicId) =>
        _gossipSubscriptions?.GetTopic(topicId) ??
        (_router ?? throw new InvalidOperationException($"{nameof(BeaconP2P)} is not started")).GetTopic(topicId);

    /// <summary>Feeds known peer addresses to the peer store so the pubsub router connects to them.</summary>
    public void Discover(Multiaddress[] addresses) => _serviceProvider.GetRequiredService<PeerStore>().Discover(addresses);

    /// <summary>The direction and agent string recorded for a live session, waiting for the identify
    /// handshake when the session is that fresh. Throws once the library has dropped
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
        LocalPeer.Session[] sessions;
        lock (localPeer.Sessions)
        {
            sessions = [.. localPeer.Sessions];
        }
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

    /// <summary>The public key the session's handshake verified, from which the peer's discv5 node id derives.</summary>
    internal static Nethermind.Libp2p.Core.Dto.PublicKey? RemotePublicKeyOf(ISession session) => (session as LocalPeer.Session)?.State.RemotePublicKey;

    /// <summary>Internal so a test can check the container gave the host the pool that request violations are reported to.</summary>
    internal IBeaconSyncPeerPool? PeerPoolForTest => _peerPool?.Value;

    /// <summary>Internal so a test can give one node a distinguishable agent string before it connects.</summary>
    internal IdentifyProtocolSettings IdentifySettingsForTest => _serviceProvider.GetRequiredService<IdentifyProtocolSettings>();

    /// <summary>Internal so a test can read the gossipsub parameters the host was built with.</summary>
    internal PubsubSettings PubsubSettingsForTest => _serviceProvider.GetRequiredService<PubsubSettings>();

    /// <summary>Internal so a test can read what a peer advertised in its identify answers.</summary>
    internal PeerStore.PeerInfo PeerInfoForTest(PeerId peerId) => _serviceProvider.GetRequiredService<PeerStore>().GetPeerInfo(peerId);

    /// <summary>Internal so a test can change the listen addresses, which makes the node push its identify to every session.</summary>
    internal LocalPeer? LocalPeerForTest => _localPeer;

    /// <summary>Internal so a test can observe a refused inbound session being torn down, not just never admitted.</summary>
    internal int SessionCountForTest => _localPeer?.Sessions.Count ?? 0;

    /// <summary>The fixed part of every request's budget; internal so a test need not wait out the production value.</summary>
    internal TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long a request's first channel may take to reach its protocol before it is opened again (see <see cref="RetryUnopenedAsync"/>).</summary>
    internal TimeSpan ChannelOpenBound { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Internal so a test can tell a session closed for an unanswered identify from a failure of the code under test.</summary>
    internal int IdentifyTimeoutsForTest => Volatile.Read(ref _identifyTimeouts);

    /// <summary>Whether the pubsub router holds a gossip channel with <paramref name="peerId"/>, opened by either side.</summary>
    internal bool HasGossipChannel(PeerId peerId) => _router is IRoutingStateContainer router && router.ConnectedPeers.Contains(peerId);

    /// <summary>The protocols <paramref name="peerId"/> listed in its identify answer; empty when none is recorded.</summary>
    internal IReadOnlyList<string> SupportedProtocolsOf(PeerId peerId) =>
        _serviceProvider.GetRequiredService<PeerStore>().GetPeerInfo(peerId)?.SupportedProtocols ?? [];

    /// <summary>Internal so a test can see which peers the started router holds a gossip connection to.</summary>
    internal IRoutingStateContainer? RoutingStateForTest => _router;

    /// <summary>Internal so a test can see the deferred validation installed on the started router.</summary>
    internal DeferredGossipValidation? DeferredValidationForTest => _deferredValidation;

    /// <summary>Internal so a test can see the validator installed on the started router; without it the node forwards every message unchecked.</summary>
    internal Func<PeerId, Libp2p.Protocols.Pubsub.Dto.Message, MessageValidity>? VerifyMessageForTest => _router?.VerifyMessage;

    /// <summary>Dials the peer, or returns the existing session when one is already established (for example inbound).</summary>
    /// <remarks>
    /// The existing-session check prefers a session whose handshake completed; the library's own check waits for one
    /// still upgrading and fails with it. The same check runs again after a failed dial: when both sides dial at once ours loses the upgrade to
    /// the session the remote opened, and that session is the connection to hand back, not a failure. The caller's own
    /// cancellation is neither: it propagates even when such a session exists.
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
        catch (Exception e) when (e is not OperationCanceledException && token.IsCancellationRequested)
        {
            throw new OperationCanceledException("The dial was cancelled by the caller", e, token);
        }
        catch (Exception) when (!token.IsCancellationRequested && remotePeerId is not null && TryGetEstablishedSession(remotePeerId, out ISession? raced)
                                && IsNotDropped(_sessionInfo, raced))
        {
            return raced;
        }
        // The library's dial skips cancelled attempts, and closing a session cancels its connection, so an empty error is a closed session.
        catch (AggregateException e) when (e.InnerExceptions.Count == 0)
        {
            throw new PeerConnectionException($"Session with {address} closed before it was established", e);
        }
    }

    /// <summary>Whether <paramref name="session"/> still has a slot whose identify did not fail.</summary>
    /// <remarks>
    /// A session whose identify failed is the one a dial lost, still closing, not a session the peer opened. Its slot is
    /// cancelled and then removed when the library drops it, which can happen between the session snapshot and this
    /// read, so a missing slot means dropped too.
    /// </remarks>
    internal static bool IsNotDropped(ConcurrentDictionary<ISession, TaskCompletionSource<SessionInfo>> sessionInfo, ISession session) =>
        sessionInfo.TryGetValue(session, out TaskCompletionSource<SessionInfo>? slot) && !slot.Task.IsCanceled;

    /// <summary>Counts the sessions with <paramref name="peerId"/> the libp2p layer opens in either direction until the watch is disposed.</summary>
    internal SessionWatch WatchSessions(PeerId peerId)
    {
        SessionWatch watch = new(this, peerId);
        _sessionWatches.TryAdd(watch, 0);
        return watch;
    }

    /// <summary>Sessions opened with one peer since <see cref="WatchSessions"/>, including ones closed again.</summary>
    internal sealed class SessionWatch(BeaconP2P owner, PeerId peerId) : IDisposable
    {
        private int _opened;

        public PeerId PeerId => peerId;

        public int Opened => Volatile.Read(ref _opened);

        internal void Count() => Interlocked.Increment(ref _opened);

        public void Dispose() => owner._sessionWatches.TryRemove(this, out _);
    }

    /// <summary>Exchanges <c>status</c> with the peer, preferring v2 and falling back to v1 (with <c>earliest_available_slot</c> of 0).</summary>
    /// <remarks>Falls back only when v2 failed as an exchange (<see cref="Eth2ReqRespException"/>) or went unanswered, which is how an unsupported
    /// protocol surfaces once the peer answers <c>na</c>; any other failure propagates.</remarks>
    public async Task<StatusMessageV2> RequestStatusAsync(ISession session, CancellationToken token, RequestTiming? timing = null)
    {
        try
        {
            using CancellationTokenSource cts = Timeout(session, token);
            return await ExchangeAsync<StatusProtocolV2, StatusMessageV2, StatusMessageV2>(session, () => Tracked(timing, CopyOf(_statusSource.CurrentStatus)), cts, token, timing);
        }
        catch (Exception e) when (IsExchangeFailure(e) || e is TimeoutException || e is OperationCanceledException && !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Status v2 with {session.RemoteAddress} failed ({e.Message}), falling back to v1");
            timing?.Restart();
            using CancellationTokenSource cts = Timeout(session, token);
            return await ExchangeAsync<StatusProtocolV1, StatusMessageV2, StatusMessageV2>(session, () => Tracked(timing, CopyOf(_statusSource.CurrentStatus)), cts, token, timing);
        }
    }

    /// <exception cref="PartialBlocksException">The request failed after some blocks arrived; it carries them.</exception>
    public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRangeAsync(ISession session, ulong startSlot, ulong count, CancellationToken token, RequestTiming? timing = null)
    {
        ConcurrentQueue<ForkedSignedBeaconBlock> received = new();
        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(count));
        try
        {
            return await ExchangeAsync<BeaconBlocksByRangeProtocolV2, BeaconBlocksByRangeDial, IReadOnlyList<ForkedSignedBeaconBlock>>(
                session, () => new BeaconBlocksByRangeDial(Tracked(timing, new BeaconBlocksByRangeRequest { StartSlot = startSlot, Count = count, Step = 1 }), received.Enqueue), cts, token, timing);
        }
        catch (Exception e) when (!token.IsCancellationRequested && !received.IsEmpty)
        {
            throw new PartialBlocksException(e, [.. received]);
        }
    }

    public async Task<IReadOnlyList<ForkedSignedBeaconBlock>> RequestBlocksByRootAsync(ISession session, Hash256[] roots, CancellationToken token, RequestTiming? timing = null)
    {
        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(roots.Length));
        return await ExchangeAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<ForkedSignedBeaconBlock>>(session, () => TrackedCopy(timing, roots), cts, token, timing);
    }

    public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRangeAsync(ISession session, ulong startSlot, ulong count, ulong[] columns, CancellationToken token, RequestTiming? timing = null)
    {
        ConcurrentQueue<DataColumnSidecar> received = new();
        // A wedged stream open is cut at the fixed bound; only a peer that has delivered a chunk earns the scaled budget.
        using CancellationTokenSource cts = Timeout(session, token);
        TimeSpan budget = DataColumnSidecarsByRangeProtocol.ResponseBudget(count, columns.Length);
        try
        {
            ForkedDataColumnSidecars sidecars = await ExchangeAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(
                session, () => new DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>(Tracked(timing, new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns }), Gloas: false, sidecar =>
                {
                    if (received.IsEmpty)
                    {
                        try
                        {
                            cts.CancelAfter(budget);
                        }
                        catch (ObjectDisposedException)
                        {
                            // The request already ended; a late chunk has nothing left to extend.
                        }
                    }

                    received.Enqueue(sidecar);
                }), cts, token, timing);
            return sidecars.Fulu;
        }
        catch (Exception e) when (!token.IsCancellationRequested && !received.IsEmpty)
        {
            throw new PartialSidecarsException(e, [.. received]);
        }
    }

    public async Task<IReadOnlyList<DataColumnSidecar>> RequestDataColumnSidecarsByRootAsync(ISession session, DataColumnsByRootIdentifier[] identifiers, CancellationToken token, RequestTiming? timing = null)
    {
        if (identifiers.Length == 0) return [];

        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(identifiers.Length));
        ForkedDataColumnSidecars sidecars = await ExchangeAsync<DataColumnSidecarsByRootProtocol, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>(
            session, () => new DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>(TrackedCopy(timing, identifiers), Gloas: false), cts, token, timing);
        return sidecars.Fulu;
    }

    /// <summary>The window must lie wholly in Gloas epochs.</summary>
    public async Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRangeAsync(ISession session, ulong startSlot, ulong count, ulong[] columns, CancellationToken token, RequestTiming? timing = null)
    {
        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(count));
        ForkedDataColumnSidecars sidecars = await ExchangeAsync<DataColumnSidecarsByRangeProtocol, DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>, ForkedDataColumnSidecars>(
            session, () => new DataColumnSidecarsDial<DataColumnSidecarsByRangeRequest>(Tracked(timing, new DataColumnSidecarsByRangeRequest { StartSlot = startSlot, Count = count, Columns = columns }), Gloas: true), cts, token, timing);
        return sidecars.Gloas;
    }

    public async Task<IReadOnlyList<DataColumnSidecarGloas>> RequestGloasDataColumnSidecarsByRootAsync(ISession session, DataColumnsByRootIdentifier[] identifiers, CancellationToken token, RequestTiming? timing = null)
    {
        if (identifiers.Length == 0) return [];

        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(identifiers.Length));
        ForkedDataColumnSidecars sidecars = await ExchangeAsync<DataColumnSidecarsByRootProtocol, DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>, ForkedDataColumnSidecars>(
            session, () => new DataColumnSidecarsDial<DataColumnsByRootIdentifier[]>(TrackedCopy(timing, identifiers), Gloas: true), cts, token, timing);
        return sidecars.Gloas;
    }

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRangeAsync(ISession session, ulong startSlot, ulong count, CancellationToken token, RequestTiming? timing = null)
    {
        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(count));
        return await ExchangeAsync<ExecutionPayloadEnvelopesByRangeProtocol, ExecutionPayloadEnvelopesByRangeRequest, IReadOnlyList<SignedExecutionPayloadEnvelope>>(
            session, () => Tracked(timing, new ExecutionPayloadEnvelopesByRangeRequest { StartSlot = startSlot, Count = count }), cts, token, timing);
    }

    public async Task<IReadOnlyList<SignedExecutionPayloadEnvelope>> RequestExecutionPayloadEnvelopesByRootAsync(ISession session, Hash256[] roots, CancellationToken token, RequestTiming? timing = null)
    {
        using CancellationTokenSource cts = Timeout(session, token, RequestTimeout + TimeSpan.FromSeconds(roots.Length));
        return await ExchangeAsync<ExecutionPayloadEnvelopesByRootProtocol, Hash256[], IReadOnlyList<SignedExecutionPayloadEnvelope>>(session, () => TrackedCopy(timing, roots), cts, token, timing);
    }

    /// <summary>Pings the peer with our metadata sequence number; returns theirs.</summary>
    public async Task<ulong> PingAsync(ISession session, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(session, token);
        return await ExchangeAsync<Eth2PingProtocol, ulong, ulong>(session, () => _metadataSource.Current.SeqNumber, cts, token, timing: null);
    }

    /// <param name="timeout">Bounds the request; the request timeout when omitted.</param>
    public async Task<MetaDataV3> RequestMetaDataAsync(ISession session, CancellationToken token, TimeSpan? timeout = null)
    {
        using CancellationTokenSource cts = Timeout(session, token, timeout);
        return await ExchangeAsync<MetaDataProtocolV3, ulong, MetaDataV3>(session, () => 0, cts, token, timing: null);
    }

    /// <summary>Sends <c>goodbye</c> best-effort; failures are ignored since the peer is being dropped anyway.</summary>
    public async Task GoodbyeAsync(ISession session, ulong reason, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout);
        try
        {
            await session.DialAsync<GoodbyeProtocol, ulong, ulong>(reason, cts.Token);
        }
        catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Goodbye to {session.RemoteAddress} failed: {e.Message}");
        }
    }

    // The pinned session dial faults its task with the protocol's AggregateException rather than the exception itself.
    private static bool IsExchangeFailure(Exception e) =>
        e is Eth2ReqRespException || (e as AggregateException)?.Flatten().InnerException is Eth2ReqRespException;

    private static CancellationTokenSource Timeout(CancellationToken token, TimeSpan timeout)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        cts.CancelAfter(timeout);
        return cts;
    }

    /// <summary>The caller's token, the session's end and the budget in one source; the request timeout when <paramref name="timeout"/> is omitted.</summary>
    private CancellationTokenSource Timeout(ISession session, CancellationToken token, TimeSpan? timeout = null)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(token, SessionClosedToken(session));
        cts.CancelAfter(timeout ?? RequestTimeout);
        return cts;
    }

    /// <summary>Cancelled once the library has dropped <paramref name="session"/>, already when it is gone; never for a session it does not own.</summary>
    internal CancellationToken SessionClosedToken(ISession session) =>
        _sessionClosed.TryGetValue(session, out SessionLifetime? closed) ? closed.Token
        : session is LocalPeer.Session ? new CancellationToken(canceled: true)
        : CancellationToken.None;

    /// <summary>Runs one exchange under <paramref name="cts"/> (see <see cref="Timeout(ISession, CancellationToken, TimeSpan?)"/>) and names why it failed.</summary>
    /// <remarks>Networking req/resp requesting side distinguishes a local cancellation, a disconnected peer and an unanswered request.</remarks>
    /// <param name="request">Builds the request for one channel; a second channel gets its own object, so the first cannot report on the second's timing.</param>
    private async Task<TResponse> ExchangeAsync<TProtocol, TRequest, TResponse>(ISession session, Func<TRequest> request, CancellationTokenSource cts, CancellationToken token, RequestTiming? timing)
        where TProtocol : ISessionProtocol<TRequest, TResponse>
    {
        long startedAt = Stopwatch.GetTimestamp();
        try
        {
            TRequest first = request();
            // Only a tracked request tells whether its channel opened; a protocol the peer does not list is refused, not dropped.
            if (timing is not { ChannelOpened: not null } tracked || !PeerMayServe<TProtocol>(session))
            {
                return await session.DialAsync<TProtocol, TRequest, TResponse>(first, cts.Token);
            }

            return await RetryUnopenedAsync(
                attempt => session.DialAsync<TProtocol, TRequest, TResponse>(first, attempt),
                tracked.TryAbandon,
                attempt =>
                {
                    tracked.Restart();
                    return session.DialAsync<TProtocol, TRequest, TResponse>(request(), attempt);
                },
                ChannelOpenBound, cts.Token);
        }
        catch (Exception e) when (!token.IsCancellationRequested && NameFailure(e, session, cts, timing, Stopwatch.GetElapsedTime(startedAt)) is { } named)
        {
            throw named;
        }
    }

    /// <summary>False only when the peer's identify answer listed protocols and <typeparamref name="TProtocol"/> is not among them.</summary>
    private bool PeerMayServe<TProtocol>(ISession session) where TProtocol : IProtocol
    {
        if (RemotePeerIdOf(session) is not { } peerId || _serviceProvider.GetService<TProtocol>() is not { } protocol)
        {
            return true;
        }

        string[]? listed = _serviceProvider.GetRequiredService<PeerStore>().GetPeerInfo(peerId).SupportedProtocols;
        return listed is not { Length: > 0 } || Array.IndexOf(listed, protocol.Id) >= 0;
    }

    /// <summary>Runs <paramref name="first"/>, and <paramref name="second"/> instead when the first channel has not reached its protocol within <paramref name="openBound"/>.</summary>
    /// <remarks>Nethermind.Libp2p.Protocols.Yamux 1.0.0 can drop the peer's first frames on a channel this node opens, so its negotiation never completes.
    /// The first attempt is cancelled before the second starts, and there is one second attempt at most.</remarks>
    /// <param name="tryAbandonFirst">Gives the first attempt up unless its channel already reached its protocol, which it then can no longer do.</param>
    internal static async Task<T> RetryUnopenedAsync<T>(Func<CancellationToken, Task<T>> first, Func<bool> tryAbandonFirst, Func<CancellationToken, Task<T>> second,
        TimeSpan openBound, CancellationToken token)
    {
        using CancellationTokenSource firstLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<T> attempt = first(firstLifetime.Token);
        try
        {
            return await attempt.WaitAsync(openBound, firstLifetime.Token);
        }
        catch (TimeoutException) when (attempt.IsCompleted || !tryAbandonFirst())
        {
            return await attempt;
        }
        catch (TimeoutException)
        {
            Interlocked.Increment(ref Metrics.ChannelsReopenedCount);
        }
        finally
        {
            // Whatever ends the wait, an unfinished first attempt is cancelled before its source is disposed or a second opens.
            if (!attempt.IsCompleted)
            {
                await firstLifetime.CancelAsync();
                _ = attempt.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
        }

        return await second(token);
    }

    /// <summary>Dials the session's identify probe, opening its channel again once when the first never reached the probe within <paramref name="openBound"/>.</summary>
    /// <returns>The peer's <c>agentVersion</c>, or <c>null</c> when its answer carries none.</returns>
    internal static Task<string?> DialIdentifyAsync(ISession session, TimeSpan openBound, CancellationToken token)
    {
        IdentifyAgentVersionProbe.Attempt firstAttempt = new();
        return RetryUnopenedAsync(
            attempt => session.DialAsync<IdentifyAgentVersionProbe, IdentifyAgentVersionProbe.Attempt, string?>(firstAttempt, attempt),
            firstAttempt.TryAbandon,
            attempt => session.DialAsync<IdentifyAgentVersionProbe, IdentifyAgentVersionProbe.Attempt, string?>(new IdentifyAgentVersionProbe.Attempt(), attempt),
            openBound, token);
    }

    /// <returns>The failure to throw in place of <paramref name="e"/>, or <c>null</c> when <paramref name="e"/> already says what happened.</returns>
    private Exception? NameFailure(Exception e, ISession session, CancellationTokenSource cts, RequestTiming? timing, TimeSpan elapsed)
    {
        string waitingFor = timing?.WaitingFor is { Length: > 0 } what ? $" {what}" : "";
        string after = $"after {ReqRespProtocolBase.Seconds(elapsed)}";
        if (SessionClosedToken(session).IsCancellationRequested && !IsExchangeFailure(e))
        {
            return new IOException($"peer disconnected {after}{waitingFor}: its libp2p session closed", e);
        }

        if ((e as AggregateException)?.Flatten().InnerException is TimeoutException protocolBound)
        {
            return protocolBound;
        }

        return e is OperationCanceledException
            ? new ReqRespTimeoutException(cts.IsCancellationRequested
                ? $"timed out {after}{waitingFor}: the request budget ran out"
                : $"ended {after}{waitingFor} without an answer: the libp2p layer cancelled the exchange", e)
            {
                ChannelNeverOpened = timing?.ChannelOpened == false,
            }
            : null;
    }

    private static T Tracked<T>(RequestTiming? timing, T request) where T : class => timing?.Track(request) ?? request;

    // The caller may pass the same array to requests running at once, and a timing must follow one request only.
    private static T[] TrackedCopy<T>(RequestTiming? timing, T[] request) => timing is null ? request : timing.Track<T[]>([.. request]);

    // The status source can hand the same object to requests running at once.
    private static StatusMessageV2 CopyOf(StatusMessageV2 status) => new()
    {
        ForkDigest = status.ForkDigest,
        FinalizedRoot = status.FinalizedRoot,
        FinalizedEpoch = status.FinalizedEpoch,
        HeadRoot = status.HeadRoot,
        HeadSlot = status.HeadSlot,
        EarliestAvailableSlot = status.EarliestAvailableSlot,
    };

    /// <summary>Loads a stored secp256k1 private key as the libp2p identity whose public key discovery derives from the same bytes.</summary>
    internal static Identity IdentityFromStoredKey(byte[] privateKey) => new(privateKey, KeyType.Secp256K1);

    private Identity LoadOrCreateIdentity()
    {
        byte[]? privateKey = _store.GetMetadata(IdentityMetadataKey);
        if (privateKey is not null)
        {
            return IdentityFromStoredKey(privateKey);
        }

        Identity identity = new(privateKey: null, KeyType.Secp256K1);
        _store.PutMetadata(IdentityMetadataKey, identity.PrivateKey!.Data.ToByteArray());
        if (_logger.IsInfo) _logger.Info($"Generated new beacon chain P2P identity {identity.PeerId}");
        return identity;
    }

    private void OnSessionConnected(ISession session)
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
                        _sessionClosed.GetOrAdd(session, static _ => new SessionLifetime());
                        CountSession(RemotePeerIdOf(session));
                        // Disposal closes only the sessions it sees, so one a dial completes afterwards is closed here, off the library's lock.
                        if (Volatile.Read(ref _disposed) == 1)
                        {
                            _ = Task.Run(session.DisconnectAsync);
                        }
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

                    if (removed is ISession closedSession && _sessionClosed.TryRemove(closedSession, out SessionLifetime? closed))
                    {
                        // Callbacks run off this thread: the library raises this event under its sessions lock.
                        _ = closed.CloseAsync();
                    }
                }

                break;
            case NotifyCollectionChangedAction.Reset:
                foreach (KeyValuePair<ISession, TaskCompletionSource<SessionInfo>> slot in _sessionInfo)
                {
                    slot.Value.TrySetCanceled();
                }

                _sessionInfo.Clear();
                foreach (KeyValuePair<ISession, SessionLifetime> closed in _sessionClosed)
                {
                    _ = closed.Value.CloseAsync();
                }

                _sessionClosed.Clear();
                break;
        }
    }

    private void CountSession(PeerId? remotePeerId)
    {
        foreach (KeyValuePair<SessionWatch, byte> watch in _sessionWatches)
        {
            if (watch.Key.PeerId.Equals(remotePeerId))
            {
                watch.Key.Count();
            }
        }
    }

    private sealed class SessionLifetime
    {
        private readonly CancellationTokenSource _closed = new();

        public CancellationToken Token { get; }

        public SessionLifetime() => Token = _closed.Token;

        public async Task CloseAsync()
        {
            try
            {
                await _closed.CancelAsync();
            }
            finally
            {
                _closed.Dispose();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Under the Sessions lock: a session added before this is in the library's disposal snapshot, one added after sees the flag.
        if (_localPeer is { } localPeer)
        {
            lock (localPeer.Sessions)
            {
                Volatile.Write(ref _disposed, 1);
            }
        }
        else
        {
            Volatile.Write(ref _disposed, 1);
        }
        if (_startCts is not null)
        {
            await _startCts.CancelAsync();
            _startCts.Dispose();
            _startCts = null;
        }

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
            // One identify exchange verifies the remote identity, fills the peer store and reads the agent string.
            _owner._sessionInfo.TryGetValue(session, out TaskCompletionSource<SessionInfo>? slot);
            string? agentVersion;
            using CancellationTokenSource cts = new(IdentifyAgentVersionProbe.ReadTimeout);
            try
            {
                agentVersion = await DialIdentifyAsync(session, _owner.ChannelOpenBound, cts.Token);
            }
            catch (Exception e)
            {
                slot?.TrySetCanceled();
                // Only the probe's own bound is a timeout, however the pending read reports it; the session's own close cancels the dial too.
                // A cancelled task would fail the dial with an empty error, as the library's dial skips cancelled attempts.
                if (cts.IsCancellationRequested)
                {
                    Interlocked.Increment(ref _owner._identifyTimeouts);
                    throw new TimeoutException($"No identify answer from {session.RemoteAddress} within {IdentifyAgentVersionProbe.ReadTimeout}", e);
                }

                throw;
            }

            slot?.TrySetResult(new SessionInfo(isDialer ? PeerDirection.Outbound : PeerDirection.Inbound, agentVersion));
        }
    }

    /// <summary>The stack as identify advertises it: without the probe, whose id duplicates identify's own.</summary>
    /// <remarks>The pinned identify lists every registered listener protocol and ignores <see cref="ProtocolRef.IsExposed"/>,
    /// so a view over the live settings is the only way to keep <c>/ipfs/id/1.0.0</c> to one entry.</remarks>
    private sealed class ProbeHidingStackSettings(IProtocolStackSettings inner) : IProtocolStackSettings
    {
        public Dictionary<ProtocolRef, ProtocolRef[]>? Protocols
        {
            get
            {
                Dictionary<ProtocolRef, ProtocolRef[]>? protocols = inner.Protocols;
                if (protocols is null)
                {
                    return null;
                }

                Dictionary<ProtocolRef, ProtocolRef[]> advertised = new(protocols.Count);
                foreach (KeyValuePair<ProtocolRef, ProtocolRef[]> protocol in protocols)
                {
                    if (protocol.Key.Protocol is not IdentifyAgentVersionProbe)
                    {
                        advertised.Add(protocol.Key, protocol.Value);
                    }
                }

                return advertised;
            }
            set => inner.Protocols = value;
        }

        public ProtocolRef[]? TopProtocols
        {
            get => inner.TopProtocols;
            set => inner.TopProtocols = value;
        }
    }

    /// <summary>Only the creation delegate is captured: the stack settings and peer store go to the base
    /// class alone, which is what lets this stay a primary constructor without double-capturing them.</summary>
    private sealed class BeaconPeerFactory(IProtocolStackSettings settings, PeerStore peerStore, IdentifyNotifier notifier, ILoggerFactory? loggerFactory, Func<Identity, ILocalPeer> create)
        : Libp2pStackPeerFactory(settings, peerStore, notifier, loggerFactory)
    {
        public override ILocalPeer Create(Identity? identity = null) => create(identity ?? new Identity(privateKey: null, KeyType.Secp256K1));
    }
}
