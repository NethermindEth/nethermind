// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Multiformats.Address;
using Nethermind.BeaconChain.P2P.Gossip;
using Nethermind.BeaconChain.P2P.ReqResp.Protocols;
using Nethermind.BeaconChain.Spec;
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

    private LocalPeer? _localPeer;
    private PubsubRouter? _router;

    public BeaconP2P(
        IBeaconChainConfig config,
        BeaconChainSpec spec,
        BeaconChainStore store,
        IBeaconChainStatusSource statusSource,
        LocalMetadataSource metadataSource,
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
            .AddLibp2p(builder => builder
                .WithPubsub()
                .AddAppLayerProtocol<StatusProtocolV1>()
                .AddAppLayerProtocol<StatusProtocolV2>()
                .AddAppLayerProtocol<GoodbyeProtocol>()
                .AddAppLayerProtocol<Eth2PingProtocol>()
                .AddAppLayerProtocol<MetaDataProtocolV3>()
                .AddAppLayerProtocol<BeaconBlocksByRangeProtocolV2>()
                .AddAppLayerProtocol<BeaconBlocksByRootProtocolV2>())
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

    /// <summary>Dials the peer, or returns the existing session when one is already established (for example inbound).</summary>
    /// <remarks>
    /// The existing-session check mirrors newer dotnet-libp2p behavior; in the pinned preview a
    /// second dial to an already-connected peer fails the upgrade with a session-exists error
    /// instead of reusing the connection.
    /// </remarks>
    public Task<ISession> DialPeerAsync(Multiaddress address, CancellationToken token)
    {
        LocalPeer localPeer = _localPeer ?? throw new InvalidOperationException($"{nameof(BeaconP2P)} is not started");
        PeerId? remotePeerId = address.GetPeerId();
        // Snapshot: the collection can change concurrently as connections come and go.
        LocalPeer.Session[] sessions = [.. localPeer.Sessions];
        foreach (ISession session in sessions)
        {
            if (session is LocalPeer.Session { State.RemotePublicKey: not null } established
                && remotePeerId is not null && remotePeerId.Equals(established.State.RemotePeerId))
            {
                return Task.FromResult(session);
            }
        }

        return localPeer.DialAsync(address, token);
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

    public async Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRangeAsync(ISession session, ulong startSlot, ulong count, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(count));
        return await session.DialAsync<BeaconBlocksByRangeProtocolV2, BeaconBlocksByRangeRequest, IReadOnlyList<SignedBeaconBlock>>(
            new BeaconBlocksByRangeRequest { StartSlot = startSlot, Count = count, Step = 1 }, cts.Token);
    }

    public async Task<IReadOnlyList<SignedBeaconBlock>> RequestBlocksByRootAsync(ISession session, Hash256[] roots, CancellationToken token)
    {
        using CancellationTokenSource cts = Timeout(token, RequestTimeout + TimeSpan.FromSeconds(roots.Length));
        return await session.DialAsync<BeaconBlocksByRootProtocolV2, Hash256[], IReadOnlyList<SignedBeaconBlock>>(roots, cts.Token);
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

    public async ValueTask DisposeAsync()
    {
        if (_localPeer is not null)
        {
            await _localPeer.DisposeAsync();
        }

        await _serviceProvider.DisposeAsync();
    }
}
