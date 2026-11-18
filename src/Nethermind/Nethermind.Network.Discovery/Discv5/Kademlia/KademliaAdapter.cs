// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Collections.Pooled;
using FastEnumUtility;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Kademlia;
using Nethermind.Network.Discovery.Discv5.Kademlia.Handlers;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Discovery.Discv5.Packets;
using Nethermind.Network.Enr;
using Nethermind.Logging;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5.Kademlia;

/// <summary>
/// Maps discv5 FINDNODE distance requests onto the protocol-specific Kademlia table.
/// </summary>
public sealed class KademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia, // Cyclic dependency: Kademlia uses this adapter as its message sender.
    IRoutingTable<Node, ValueHash256> routingTable, // Direct hash lookup also works before a packet's public key is authenticated.
    DiscoveryV5Transport transport,
    PacketCodec packetCodec,
    INodeRecordProvider nodeRecordProvider,
    IDiscoveryConfig discoveryConfig,
    KademliaConfig<Node> kademliaConfig,
    ICryptoRandom cryptoRandom,
    IKademliaDistance<ValueHash256> distance,
    ILogManager logManager,
    NetworkListenerState listenerState,
    PeerAddressPolicy addressPolicy
) : KademliaAdapterBase("discv5", logManager.GetClassLogger<KademliaAdapter>(), listenerState), IKademliaAdapter
{

    private PeerAddressPermissions AllowsNonRoutableTargets => addressPolicy.AllowedAddresses;

    private const int MaxFindNodeRecords = 16;
    private const int MaxFindNodeDistances = 8;
    private const int MaxEnrsPerNodesMessage = 3;
    private const int MaxSessions = 4_096;
    private const int MaxSentChallenges = 4_096;
    private const int MaxPendingRequests = 4_096;
    private const int MaxResponseHandlers = 1_024;
    private const int PacketWorkerCount = 4;
    private static readonly TimeSpan SentChallengeTtl = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ChallengeRateLimitWindow = TimeSpan.FromMilliseconds(100);
    private const int ChallengeRateLimitBurstPerIp = 16;
    private const int ChallengeRateLimitFilterSize = 8_192;

    internal DiscoveryRequestActivity RequestActivity { get; set; } = new();

    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _findNodeTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly IKademliaDistance<ValueHash256> _distance = distance;
    private readonly ValueHash256 _currentNodeHash = kademliaConfig.CurrentNodeId.Id.Hash.ValueHash256;
    private readonly DisposingLruCache<SessionKey, Session> _sessions = new(MaxSessions, "discv5 sessions");
    private readonly LruCache<ChallengeKey, SentChallenge> _sentChallenges = new(MaxSentChallenges, "discv5 sent challenges");
    private readonly Lock _sentChallengesLock = new();
    private readonly LruCache<PendingNonceKey, PendingRequest> _pendingByNonce = new(MaxPendingRequests, "discv5 pending requests");
    private readonly Dictionary<ResponseKey, IResponseHandler> _responseHandlers = [with(MaxResponseHandlers)];
    private readonly Lock _responseHandlersLock = new();
    private readonly EndpointCheckLimiter _endpointChecks = new(MaxResponseHandlers / 4);
    private readonly AddressBurstLimiter _challengeRateLimiter = new(ChallengeRateLimitBurstPerIp, ChallengeRateLimitFilterSize, ChallengeRateLimitWindow);

    internal INodeRecordSigner RecordSigner { private get; init; } = new NodeRecordSigner(new Ecdsa());
    internal int RetainedChallengeCount => _sentChallenges.Count;
    internal TimeProvider RequestTimeProvider
    {
        get => _endpointChecks.TimeProvider;
        init => _endpointChecks.TimeProvider = value;
    }

    internal int ResponseHandlerCount
    {
        get { lock (_responseHandlersLock) return _responseHandlers.Count; }
    }

    /// <inheritdoc/>
    protected override bool TryReserveRemoteRecordRefresh(Node node, out IDisposable? lease)
        => _endpointChecks.TryReserveRefresh(node, out lease);

    internal TimeProvider ChallengeTimeProvider { get; init; } = TimeProvider.System;
    internal AddressBurstLimiter? ChallengeLimiter { private get; init; }

    /// <inheritdoc/>
    public Node[] GetNodesAtDistances(IEnumerable<int> distances, Node? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(distances);

        using PooledSet<Hash256> seen = new(MaxFindNodeRecords);
        using ArrayPoolListRef<Node> result = new(MaxFindNodeRecords);
        Hash256? excludedHash = excluding?.IdHash;

        const int maxDistances = 257;
        Span<bool> requested = stackalloc bool[maxDistances];
        requested.Clear();
        Span<int> uniqueDistances = stackalloc int[MaxFindNodeDistances];
        int inputCount = 0;
        int uniqueCount = 0;
        foreach (int distance in distances)
        {
            if (++inputCount > maxDistances)
            {
                ThrowTooManyDistances(maxDistances, nameof(distances));
            }
            if ((uint)distance > (uint)_distance.MaxDistance || distance >= maxDistances)
            {
                throw new ArgumentOutOfRangeException(nameof(distances), distance, $"Distance must be between 0 and {_distance.MaxDistance}.");
            }
            if (requested[distance]) continue;
            requested[distance] = true;
            if (uniqueCount < MaxFindNodeDistances) uniqueDistances[uniqueCount++] = distance;
        }

        foreach (int distance in uniqueDistances[..uniqueCount])
        {
            Node[] nodes = kademlia.Value.GetAllAtDistance(distance);
            for (int i = 0; i < nodes.Length; i++)
            {
                Node node = nodes[i];

                if (excludedHash is not null && node.IdHash.Equals(excludedHash))
                {
                    continue;
                }

                if (seen.Add(node.IdHash))
                {
                    result.Add(node);
                    if (result.Count == MaxFindNodeRecords) return result.ToArray();
                }
            }
        }

        return result.Count == 0 ? [] : result.ToArray();
    }

    /// <inheritdoc/>
    public async Task<bool> Ping(Node receiver, CancellationToken token)
    {
        ulong? sequence = await PingRoundTrip(receiver, token);
        if (sequence is null) return false;
        ReserveEndpointCheck(receiver);
        await RefreshRemoteRecordIfNewer(receiver, sequence.Value, token);
        return true;
    }

    private async Task<ulong?> PingRoundTrip(Node receiver, CancellationToken token)
    {
        using PingMsg ping = new(CreateRequestId(), (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence);
        PongResponseHandler responseHandler = new(receiver);
        if (Logger.IsTrace) Logger.Trace($"Sending discv5 PING {ping.RequestId} to {receiver.ToString("s"):hide}.");
        if (!await SendRequest(receiver, ping, responseHandler, _pingTimeout, token))
        {
            token.ThrowIfCancellationRequested();
            if (Logger.IsTrace) Logger.Trace($"Discv5 PING {ping.RequestId} to {receiver.ToString("s"):hide} timed out.");
            return null;
        }
        if (Logger.IsTrace) Logger.Trace($"Discv5 PING {ping.RequestId} to {receiver.ToString("s"):hide} succeeded.");
        DiscoveryLiveness.Mark(receiver);
        AddOrRefreshRemoteNode(receiver);
        return responseHandler.EnrSequence;
    }

    /// <inheritdoc/>
    public async Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        Distances distances = GetLookupDistances(receiver, target);
        using FindNodeMsg findNode = new(CreateRequestId(), distances);
        using NodesResponseHandler responseHandler = new(receiver, distances, _distance, LocalIp, AllowsNonRoutableTargets, kademliaConfig.CurrentNodeId.Id, routingTable, RecordSigner);

        if (Logger.IsTrace) Logger.Trace($"Sending discv5 FINDNODE {findNode.RequestId} to {receiver.ToString("s"):hide}, distances: {FormatDistances(distances)}.");
        if (!await SendRequest(receiver, findNode, responseHandler, _findNodeTimeout, token))
        {
            token.ThrowIfCancellationRequested();
            if (Logger.IsTrace) Logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver.ToString("s"):hide} timed out.");
            return null;
        }

        Node[] nodes = responseHandler.GetNodes();
        receiver.ValidatedProtocol = true;
        DiscoveryLiveness.Mark(receiver);

        if (Logger.IsTrace) Logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver.ToString("s"):hide} returned {nodes.Length} nodes.");
        return nodes;
    }

    public async Task RunAsync(CancellationToken token)
    {
        Task[] workers = new Task[PacketWorkerCount];
        for (int i = 0; i < workers.Length; i++)
        {
            workers[i] = RunPacketWorkerAsync(token);
        }

        await Task.WhenAll(workers);
    }

    private async Task RunPacketWorkerAsync(CancellationToken token)
    {
        try
        {
            await foreach (PooledUdpReceiveResult result in transport.ReadMessagesAsync(token))
            {
                try
                {
                    await HandlePacket(result, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    if (Logger.IsTrace) Logger.Trace($"Error handling discv5 packet from {result.RemoteEndPoint:hide}: {e:hide}");
                }
                finally
                {
                    result.Dispose();
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (Logger.IsError) Logger.Error("Error in discv5 packet loop", e);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _sessions.Clear();
        return ValueTask.CompletedTask;
    }

    private async Task<bool> SendRequest<TResponse>(
        Node receiver,
        Discv5Message request,
        IResponseHandler<TResponse> responseHandler,
        TimeSpan timeout,
        CancellationToken token)
        where TResponse : Discv5Message
    {
        if (token.IsCancellationRequested) return false;
        long onlineGeneration = RequestActivity.BeginRequest();
        ResponseKey responseKey = new(receiver.Id.Hash.ValueHash256, receiver.DiscoveryAddress, request.RequestId, responseHandler.MessageType);
        lock (_responseHandlersLock)
        {
            if (_responseHandlers.Count >= MaxResponseHandlers || !_responseHandlers.TryAdd(responseKey, responseHandler))
            {
                ThrowRequestNotSent();
            }
        }

        using CancellationTokenSource deadline = new(timeout, RequestTimeProvider);
        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);

        PendingNonceKey? pendingNonceKey = null;
        try
        {
            pendingNonceKey = await SendMessage(receiver, request, responseHandler, timeoutCts.Token);
            Task response = responseHandler.Task.WaitAsync(timeoutCts.Token);
            await response.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            if (response.IsCanceled && timeoutCts.IsCancellationRequested)
            {
                if (!token.IsCancellationRequested && Logger.IsTrace) TraceRequestTimeout(receiver, request, timeout);
                return false;
            }

            await response;
            RequestActivity.Answered(onlineGeneration);
            return true;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            if (!token.IsCancellationRequested && Logger.IsTrace) TraceRequestTimeout(receiver, request, timeout);
            return false;
        }
        finally
        {
            lock (_responseHandlersLock)
            {
                _responseHandlers.Remove(responseKey);
                // Cancellation can win the wait while a local encode refusal faults the handler.
                _ = responseHandler.Task.Exception;
            }
            if (pendingNonceKey is not null)
            {
                _pendingByNonce.TryRemove(pendingNonceKey.Value, out _);
            }
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void TraceRequestTimeout(Node receiver, Discv5Message request, TimeSpan timeout)
        => Logger.Trace($"Discv5 request {request.MessageType} {request.RequestId} to {receiver.ToString("s"):hide} timed out after {timeout}.");

    private async Task<PendingNonceKey?> SendMessage(Node receiver, Discv5Message message, IResponseHandler responseHandler, CancellationToken token)
    {
        if (TryEncodeWithExistingSession(receiver, message, out PendingNonceKey pendingNonceKey, out byte[]? packet))
        {
            return await SendPendingPacket(receiver, message, responseHandler, pendingNonceKey, packet, hasSession: true, token);
        }

        pendingNonceKey = EncodeMessageWithoutSession(receiver, message, out byte[] initialPacket);
        return await SendPendingPacket(receiver, message, responseHandler, pendingNonceKey, initialPacket, hasSession: false, token);
    }

    [SkipLocalsInit]
    private bool TryEncodeWithExistingSession(
        Node receiver,
        Discv5Message message,
        out PendingNonceKey pendingNonceKey,
        [NotNullWhen(true)] out byte[]? packet)
    {
        SessionKey sessionKey = new(receiver.Id.Hash.ValueHash256, receiver.DiscoveryAddress);
        if (TryGetSession(sessionKey, out Session? session))
        {
            Span<byte> writeKey = stackalloc byte[Session.KeySize];
            if (session.TryCopyWriteKey(writeKey))
            {
                Span<byte> sessionNonce = stackalloc byte[PacketCodec.NonceSize];
                session.WriteNextNonce(cryptoRandom, sessionNonce);
                pendingNonceKey = new PendingNonceKey(receiver.DiscoveryAddress, NonceKey.From(sessionNonce));
                packet = packetCodec.EncodeOrdinary(receiver.Id, writeKey, message, sessionNonce);
                return true;
            }
        }

        pendingNonceKey = default;
        packet = null;
        return false;
    }

    [SkipLocalsInit]
    private PendingNonceKey EncodeMessageWithoutSession(Node receiver, Discv5Message message, out byte[] initialPacket)
    {
        Span<byte> nonce = stackalloc byte[PacketCodec.NonceSize];
        cryptoRandom.GenerateRandomBytes(nonce);
        Span<byte> encryptionKey = stackalloc byte[Session.KeySize];
        cryptoRandom.GenerateRandomBytes(encryptionKey);
        PendingNonceKey pendingNonceKey = new(receiver.DiscoveryAddress, NonceKey.From(nonce));
        initialPacket = packetCodec.EncodeOrdinary(receiver.Id, encryptionKey, message, nonce);
        return pendingNonceKey;
    }

    private async Task<PendingNonceKey> SendPendingPacket(
        Node receiver,
        Discv5Message message,
        IResponseHandler responseHandler,
        PendingNonceKey pendingNonceKey,
        byte[] packet,
        bool hasSession,
        CancellationToken token)
    {
        if (packet.Length == 0) ThrowRequestNotSent();
        _pendingByNonce.Set(pendingNonceKey, new PendingRequest(receiver, message, responseHandler));
        try
        {
            if (Logger.IsTrace) Logger.Trace($"Sending discv5 ordinary {message.MessageType} {message.RequestId} to {receiver.ToString("s"):hide} {(hasSession ? "with existing session" : "without session")}, bytes: {packet.Length}.");
            await transport.SendAsync(packet, receiver.DiscoveryAddress, token);
            RecordSent(message);
            return pendingNonceKey;
        }
        catch
        {
            _pendingByNonce.TryRemove(pendingNonceKey, out _);
            throw;
        }
    }

    private async Task SendResponse(Node receiver, Discv5Message message, CancellationToken token)
    {
        if (!TryEncodeResponse(receiver, message, out byte[]? packet))
        {
            return;
        }

        if (Logger.IsTrace) Logger.Trace($"Sending discv5 response {message.MessageType} {message.RequestId} to {receiver.ToString("s"):hide}, bytes: {packet.Length}.");
        await transport.SendAsync(packet, receiver.DiscoveryAddress, token);
        RecordSent(message);
    }

    [SkipLocalsInit]
    private bool TryEncodeResponse(Node receiver, Discv5Message message, [NotNullWhen(true)] out byte[]? packet)
    {
        SessionKey sessionKey = new(receiver.Id.Hash.ValueHash256, receiver.DiscoveryAddress);
        if (!TryGetSession(sessionKey, out Session? session))
        {
            packet = null;
            return false;
        }

        Span<byte> writeKey = stackalloc byte[Session.KeySize];
        if (!session.TryCopyWriteKey(writeKey))
        {
            packet = null;
            return false;
        }

        Span<byte> nonce = stackalloc byte[PacketCodec.NonceSize];
        session.WriteNextNonce(cryptoRandom, nonce);
        packet = packetCodec.EncodeOrdinary(receiver.Id, writeKey, message, nonce);
        return packet.Length != 0;
    }

    internal async Task HandlePacket(PooledUdpReceiveResult udpPacket, CancellationToken token)
    {
        if (!packetCodec.TryDecode(udpPacket.Buffer, out Packet packet))
        {
            if (Logger.IsTrace) Logger.Trace($"Dropping undecodable discv5 packet from {udpPacket.RemoteEndPoint:hide}, bytes: {udpPacket.Buffer.Length}.");
            return;
        }

        using (packet)
        {
            if (Logger.IsTrace) Logger.Trace($"Received discv5 {packet.Flag} packet from {udpPacket.RemoteEndPoint:hide}, bytes: {udpPacket.Buffer.Length}.");
            try
            {
                switch (packet.Flag)
                {
                    case PacketFlag.WhoAreYou:
                        await HandleWhoAreYou(udpPacket.RemoteEndPoint, packet, token);
                        break;
                    case PacketFlag.Ordinary:
                        await HandleOrdinary(udpPacket.RemoteEndPoint, packet, token);
                        break;
                    case PacketFlag.Handshake:
                        await HandleHandshake(udpPacket.RemoteEndPoint, packet, token);
                        break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception e)
            {
                if (Logger.IsDebug) Logger.Debug($"Error handling discv5 packet from {udpPacket.RemoteEndPoint:hide}: {e:hide}");
            }
        }
    }

    private async Task HandleWhoAreYou(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        PendingNonceKey pendingNonceKey = new(endpoint, NonceKey.From(packet.Nonce.Span));
        if (!_pendingByNonce.TryRemove(pendingNonceKey, out PendingRequest? pendingRequest))
        {
            if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 WHOAREYOU from {endpoint:hide}; no pending request for nonce.");
            return;
        }

        byte[] handshakePacket;
        Session session;
        ulong requestedEnrSequence;
        using (Challenge challenge = packetCodec.DecodeWhoAreYou(in packet))
        {
            NodeRecord currentNodeRecord = await nodeRecordProvider.GetCurrentAsync(token);
            handshakePacket = packetCodec.EncodeHandshake(pendingRequest.Receiver.Id, challenge, pendingRequest.Message, currentNodeRecord, out session);
            requestedEnrSequence = challenge.EnrSequence;
        }

        if (handshakePacket.Length == 0)
        {
            session.Dispose();
            ResponseKey responseKey = new(pendingRequest.Receiver.Id.Hash.ValueHash256, endpoint,
                pendingRequest.Message.RequestId, pendingRequest.ResponseHandler.MessageType);
            lock (_responseHandlersLock)
            {
                if (_responseHandlers.TryGetValue(responseKey, out IResponseHandler? handler) &&
                    ReferenceEquals(handler, pendingRequest.ResponseHandler))
                {
                    handler.Fail(new KademliaRequestNotSentException());
                }
            }
            return;
        }

        SetSession(new SessionKey(pendingRequest.Receiver.Id.Hash.ValueHash256, endpoint), session);
        if (Logger.IsTrace) Logger.Trace($"Sending discv5 HANDSHAKE for {pendingRequest.Message.MessageType} {pendingRequest.Message.RequestId} to {endpoint:hide}, bytes: {handshakePacket.Length}, requested ENR seq: {requestedEnrSequence}.");
        await transport.SendAsync(handshakePacket, endpoint, token);
        RecordSent("Handshake");
    }

    private async Task HandleOrdinary(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        if (!PacketCodec.TryGetSourceNodeId(in packet, out ValueHash256 nodeId))
        {
            if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 ordinary packet from {endpoint:hide}; source node id missing.");
            return;
        }

        SessionKey sessionKey = new(nodeId, endpoint);
        if (!TryDecryptOrdinaryMessage(in packet, sessionKey, out Session? session, out Discv5Message? message))
        {
            if (Logger.IsTrace) Logger.Trace($"Discv5 ordinary packet from {endpoint:hide} could not be decrypted with an existing session; sending WHOAREYOU.");
            await SendWhoAreYou(endpoint, packet, nodeId, token);
            return;
        }

        try
        {
            if (Logger.IsTrace) Logger.Trace($"Received discv5 message {message.MessageType} {message.RequestId} from {endpoint:hide}.");
            await HandleMessage(session.RemotePublicKey, endpoint, message, token);
        }
        finally
        {
            message.Dispose();
        }
    }

    [SkipLocalsInit]
    private bool TryDecryptOrdinaryMessage(scoped in Packet packet, SessionKey sessionKey, [NotNullWhen(true)] out Session? session, [NotNullWhen(true)] out Discv5Message? message)
    {
        Span<byte> readKey = stackalloc byte[Session.KeySize];
        if (TryGetSession(sessionKey, out session) &&
            session.TryCopyReadKey(readKey) &&
            PacketCodec.TryDecryptMessage(in packet, readKey, out Discv5Message decodedMessage))
        {
            message = decodedMessage;
            return true;
        }

        message = null;
        return false;
    }

    private async Task HandleHandshake(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        if (!PacketCodec.TryGetSourceNodeId(in packet, out ValueHash256 nodeId))
        {
            if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 handshake packet from {endpoint:hide}; source node id missing.");
            return;
        }

        ChallengeKey challengeKey = new(nodeId, endpoint);
        if (!_sentChallenges.TryGet(challengeKey, out SentChallenge sentChallenge))
        {
            if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 handshake packet from {endpoint:hide}; matching challenge missing.");
            return;
        }

        if (IsExpired(sentChallenge))
        {
            if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 handshake packet from {endpoint:hide}; matching challenge expired.");
            return;
        }

        if (!transport.TryAcceptHandshake()) return;

        TryGetKnownNode(nodeId, out Node? knownNode);
        NodeRecord? knownRecord = knownNode?.Enr is { Signature: not null } signedRecord ? signedRecord : null;
        if (!PacketCodec.TryDecode(sentChallenge.Packet, nodeId.Bytes, out Packet challengePacket))
        {
            if (Logger.IsTrace) Logger.Trace($"Unable to decode matching discv5 WHOAREYOU challenge for {endpoint:hide}.");
            return;
        }

        Session session;
        Discv5Message message;
        NodeRecord? nodeRecord;
        using (challengePacket)
        using (Challenge challenge = packetCodec.DecodeWhoAreYou(in challengePacket))
        {
            if (!packetCodec.TryDecryptHandshake(in packet, challenge, knownRecord, out session, out message, out nodeRecord))
            {
                if (Logger.IsTrace) Logger.Trace($"Unable to decrypt discv5 handshake packet from {endpoint:hide}.");
                return;
            }
        }

        bool consumed;
        lock (_sentChallengesLock)
        {
            consumed = !IsExpired(sentChallenge)
                && _sentChallenges.TryGet(challengeKey, out SentChallenge currentChallenge)
                && ReferenceEquals(currentChallenge.Packet, sentChallenge.Packet)
                && _sentChallenges.TryRemove(challengeKey, out _);
        }
        if (!consumed)
        {
            session.Dispose();
            message.Dispose();
            return;
        }

        await HandleHandshakeMessage(endpoint, nodeId, session, message, nodeRecord, token);
    }

    private async Task SendWhoAreYou(IPEndPoint endpoint, Packet requestPacket, ValueHash256 nodeId, CancellationToken token)
    {
        ChallengeKey challengeKey = new(nodeId, endpoint);
        if (!TryAcceptChallenge(endpoint))
        {
            if (Logger.IsTrace) Logger.Trace($"Rate limiting discv5 WHOAREYOU challenge to {endpoint:hide}.");
            return;
        }

        if (!transport.TryAcceptChallenge()) return;

        ulong enrSequence = GetChallengeEnrSequence(nodeId, endpoint);
        byte[] packet = packetCodec.EncodeWhoAreYou(nodeId.Bytes, requestPacket.Nonce.Span, enrSequence);
        SetSentChallenge(challengeKey, packet);
        if (Logger.IsTrace) Logger.Trace($"Sending discv5 WHOAREYOU challenge to {endpoint:hide}, known ENR seq: {enrSequence}, bytes: {packet.Length}.");
        await transport.SendAsync(packet, endpoint, token);
        RecordSent("WhoAreYou");
    }

    internal ulong GetChallengeEnrSequence(ValueHash256 nodeId, IPEndPoint endpoint)
    {
        if (!TryGetKnownNode(nodeId, out Node? knownNode) ||
            knownNode.Enr is not { Signature: not null } record ||
            !knownNode.IsVerifiedEnr(record))
        {
            return 0UL;
        }

        return HasDiscoveryEndpoint(record, endpoint) ? knownNode.HighestObservedEnrSequence : 0UL;
    }

    private static void RecordSent(Discv5Message message) => RecordSent(FastEnum.GetName(message.MessageType)!);

    private static void RecordSent(string messageType)
        => Metrics.DiscoveryMessagesSentByProtocol.Increment(new DiscoveryMessageKey("discv5", messageType));

    private async Task HandleHandshakeMessage(
        IPEndPoint endpoint,
        ValueHash256 nodeId,
        Session session,
        Discv5Message message,
        NodeRecord? nodeRecord,
        CancellationToken token)
    {
        bool sessionStored = false;
        try
        {
            NodeRecord? messageRecord = null;
            ulong observedEnrSequence = 0;
            if (nodeRecord is not null)
            {
                if (!HasExpectedNodeId(nodeRecord, nodeId))
                {
                    if (Logger.IsTrace) Logger.Trace($"Ignoring discv5 handshake ENR from {endpoint:hide}; ENR node id does not match packet source.");
                    return;
                }

                // PacketCodec verified the ENR signature and handshake identity. Preserve its
                // sequence even when this listener cannot use the record's endpoint or the record
                // does not advertise the packet source.
                observedEnrSequence = nodeRecord.EnrSequence;
                if (TryGetAcceptableNode(
                    nodeRecord,
                    AllowsNonRoutableTargets,
                    LocalIp,
                    endpoint,
                    out _))
                {
                    messageRecord = nodeRecord;
                }
            }

            SetSession(new SessionKey(nodeId, endpoint), session);
            sessionStored = true;
            if (Logger.IsTrace) Logger.Trace($"Received discv5 handshake message {message.MessageType} {message.RequestId} from {endpoint:hide}, ENR included: {nodeRecord is not null}.");
            await HandleMessage(session.RemotePublicKey, endpoint, message, token, messageRecord, observedEnrSequence);
        }
        finally
        {
            if (!sessionStored)
            {
                session.Dispose();
            }

            message.Dispose();
        }
    }

    private async Task HandleMessage(
        PublicKey remotePublicKey,
        IPEndPoint endpoint,
        Discv5Message message,
        CancellationToken token,
        NodeRecord? nodeRecord = null,
        ulong observedEnrSequence = 0)
    {
        ValueHash256 remoteNodeId = remotePublicKey.Hash.ValueHash256;
        Node remoteNode = Node.FromDiscoveryEndpoint(remotePublicKey, endpoint);
        bool knownPeer = false;
        if (routingTable.TryGet(remoteNodeId, out Node? knownNode))
        {
            knownPeer = true;
            // Routing refreshes replace Node objects. Sharing this identity's ENR state keeps
            // concurrent packet workers and an awaiting record refresh on one atomic cache.
            remoteNode.MergeEnrStateFrom(knownNode);
            if (remoteNode.DiscoveryAddress.Equals(knownNode.DiscoveryAddress)) remoteNode.ValidatedProtocol = knownNode.ValidatedProtocol;
        }

        if (observedEnrSequence > remoteNode.HighestObservedEnrSequence)
        {
            // Keep verified sequence state on an existing routing entry even for request messages
            // that intentionally do not admit a new node to the table.
            remoteNode.ObserveEnrSequence(observedEnrSequence);
        }

        if (nodeRecord is { Signature: not null } verifiedHandshakeRecord)
        {
            remoteNode.SetVerifiedEnr(verifiedHandshakeRecord);
        }

        if (HandleResponse(remoteNodeId, remoteNode.DiscoveryAddress, message))
        {
            if (Logger.IsTrace) Logger.Trace($"Handled discv5 response {message.MessageType} {message.RequestId} from {endpoint:hide}.");
            remoteNode.ValidatedProtocol = true;
            DiscoveryLiveness.Mark(remoteNode);
            AddOrRefreshRemoteNode(remoteNode);
            return;
        }

        if (Logger.IsTrace) Logger.Trace($"Handling discv5 request {message.MessageType} {message.RequestId} from {endpoint:hide}.");
        switch (message)
        {
            case PingMsg ping:
                using (PongMsg pong = new(ping.RequestId, (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence, endpoint.Address, endpoint.Port))
                {
                    await SendResponse(remoteNode, pong, token);
                }

                AddOrRefreshRemoteNode(remoteNode);
                if (knownPeer) StartRemoteRecordRefresh(remoteNode, ping.EnrSequence, token);
                StartEndpointCheck(remoteNode, token);
                break;
            case FindNodeMsg findNode:
                await HandleFindNode(remoteNode, findNode, token);
                break;
            case TalkReqMsg talkReq:
                using (TalkRespMsg talkResp = new(talkReq.RequestId, ReadOnlyMemory<byte>.Empty))
                {
                    await SendResponse(remoteNode, talkResp, token);
                }

                break;
        }
    }

    private void StartRemoteRecordRefresh(Node node, ulong advertisedSequence, CancellationToken token)
        => _ = RunRemoteRecordRefresh(node, advertisedSequence, token);

    private async Task RunRemoteRecordRefresh(Node node, ulong advertisedSequence, CancellationToken token)
    {
        try
        {
            await RefreshRemoteRecordIfNewer(node, advertisedSequence, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    protected override async ValueTask<NodeRecord?> RequestRemoteRecord(Node node, ulong requestedSequence, CancellationToken token)
    {
        using FindNodeMsg findNode = new(CreateRequestId(), new Distances([0]));
        using SelfRecordResponseHandler responseHandler = new(node, requestedSequence, RecordSigner);
        if (!await SendRequest(node, findNode, responseHandler, _findNodeTimeout, token))
        {
            return null;
        }

        return responseHandler.GetRecord();
    }

    protected override bool TryCreateNodeFromEnr(Node currentNode, NodeRecord record, [NotNullWhen(true)] out Node? refreshedNode)
        => TryGetAcceptableNode(
            record,
            currentNode.IsStatic || currentNode.IsTrusted || currentNode.IsBootnode ? PeerAddressPermissions.All : AllowsNonRoutableTargets,
            LocalIp,
            currentNode.DiscoveryAddress,
            out refreshedNode);

    protected override void AddOrRefreshRemoteNode(Node node)
    {
        if (node.ValidatedProtocol is not null) kademlia.Value.AddOrRefresh(node);
    }

    protected override async ValueTask<bool> VerifyRemoteEndpoint(Node node, CancellationToken token)
    {
        if (routingTable.TryGet(node.Id.Hash.ValueHash256, out Node? known) && node.DiscoveryAddress.Equals(known.DiscoveryAddress))
        {
            node.ValidatedProtocol ??= known.ValidatedProtocol;
            DiscoveryLiveness.CarryProof(node, known);
        }
        if (node.ValidatedProtocol is not null) return true;
        if (!_endpointChecks.TryReserveEndpoint(node, out IDisposable? lease)) return false;
        using (lease) return await PingRoundTrip(node, token) is not null;
    }

    private bool HandleResponse(ValueHash256 nodeId, IPEndPoint endpoint, Discv5Message message)
    {
        ResponseKey responseKey = new(nodeId, endpoint, message.RequestId, message.MessageType);
        IResponseHandler? handler;
        lock (_responseHandlersLock) _responseHandlers.TryGetValue(responseKey, out handler);
        return handler is not null && handler.Handle(message);
    }

    private async Task HandleFindNode(Node remoteNode, FindNodeMsg findNode, CancellationToken token)
    {
        NodeRecord selfRecord = await nodeRecordProvider.GetCurrentAsync(token);
        NodeRecord[] records = GetFindNodeRecords(findNode.Distances, remoteNode, selfRecord);
        if (records.Length == 0)
        {
            using NodesMsg emptyResponse = new(findNode.RequestId, 1, []);
            await SendResponse(remoteNode, emptyResponse, token);
            return;
        }

        int total = (records.Length + MaxEnrsPerNodesMessage - 1) / MaxEnrsPerNodesMessage;
        for (int i = 0; i < records.Length; i += MaxEnrsPerNodesMessage)
        {
            int count = Math.Min(MaxEnrsPerNodesMessage, records.Length - i);
            ArraySegment<NodeRecord> chunk = new(records, i, count);
            using NodesMsg nodes = new(findNode.RequestId, total, chunk);
            await SendResponse(remoteNode, nodes, token);
        }
    }

    private NodeRecord[] GetFindNodeRecords(Distances distances, Node requester, NodeRecord selfRecord)
    {
        using PooledSet<Hash256> seen = new(MaxFindNodeRecords);
        ArrayPoolListRef<NodeRecord> result = new(MaxFindNodeRecords);
        try
        {
            PeerAddressPermissions allowNonRoutableRelays = requester.DiscoveryAddress.Address.IsLoopbackOrPrivateOrLinkLocal ? AllowsNonRoutableTargets : PeerAddressPermissions.None;
            Span<bool> requested = stackalloc bool[Distances.MaxCount];
            requested.Clear();
            int uniqueCount = 0;
            for (int i = 0; i < distances.Count && result.Count < MaxFindNodeRecords && uniqueCount < MaxFindNodeDistances; i++)
            {
                int distance = distances[i];
                if ((uint)distance > (uint)_distance.MaxDistance || (uint)distance >= Distances.MaxCount || requested[distance])
                {
                    continue;
                }
                requested[distance] = true;
                uniqueCount++;

                if (distance == 0)
                {
                    result.Add(selfRecord);
                    continue;
                }

                AddFindNodeRecordsAtDistance(distance, requester, allowNonRoutableRelays, seen, ref result);
            }

            return result.Count == 0 ? [] : result.ToArray();
        }
        finally
        {
            result.Dispose();
        }
    }

    private void AddFindNodeRecordsAtDistance(
        int distance,
        Node requester,
        PeerAddressPermissions allowNonRoutableRelays,
        PooledSet<Hash256> seen,
        ref ArrayPoolListRef<NodeRecord> result)
    {
        Node[] nodes = kademlia.Value.GetAllAtDistance(distance);
        Hash256 requesterHash = requester.IdHash;
        AddFindNodeRecords(nodes, requesterHash, allowNonRoutableRelays, seen, ref result, onlyValidated: true);
        AddFindNodeRecords(nodes, requesterHash, allowNonRoutableRelays, seen, ref result, onlyValidated: false);
    }

    private void AddFindNodeRecords(
        Node[] nodes,
        Hash256 requesterHash,
        PeerAddressPermissions allowNonRoutableRelays,
        PooledSet<Hash256> seen,
        ref ArrayPoolListRef<NodeRecord> result,
        bool onlyValidated)
    {
        for (int i = 0; i < nodes.Length && result.Count < MaxFindNodeRecords; i++)
        {
            Node node = nodes[i];

            if ((node.ValidatedProtocol == true) != onlyValidated)
            {
                continue;
            }

            if (node.IdHash.Equals(requesterHash) || node.Enr is not { Signature: not null } || !seen.Add(node.Id.Hash))
            {
                continue;
            }

            NodeRecord? record = GetFindNodeRecord(node, allowNonRoutableRelays);
            if (record is not null)
            {
                result.Add(record);
            }
        }
    }

    internal static NodeRecord? GetFindNodeRecord(Node node, PeerAddressPermissions allowNonRoutableRelays)
    {
        NodeRecord? record = node.Enr;
        return record is not null &&
               record.EnrSequence >= node.HighestObservedEnrSequence &&
               IsAcceptableNodeRecord(record, node.Id.Hash, allowNonRoutableRelays)
            ? record
            : null;
    }

    [SkipLocalsInit]
    internal Distances GetLookupDistances(Node receiver, PublicKey target)
    {
        int distance = _distance.CalculateLogDistance(receiver.Id.Hash.ValueHash256, target.Hash.ValueHash256);

        Span<int> distances = stackalloc int[3];
        distances[0] = distance;
        int count = 1;
        if (distance > 0)
        {
            distances[count++] = distance - 1;
        }

        if (distance < _distance.MaxDistance)
        {
            distances[count++] = distance + 1;
        }

        return new Distances(distances[..count]);
    }

    [SkipLocalsInit]
    private static string FormatDistances(Distances distances)
    {
        Span<char> chars = stackalloc char[16];
        int position = 0;
        for (int i = 0; i < distances.Count; i++)
        {
            if (i > 0)
            {
                chars[position++] = ',';
            }

            if (!distances[i].TryFormat(chars[position..], out int written))
            {
                return string.Join(",", distances);
            }

            position += written;
        }

        return chars[..position].ToString();
    }

    [SkipLocalsInit]
    private RequestId CreateRequestId()
    {
        Span<byte> requestId = stackalloc byte[sizeof(ulong)];
        cryptoRandom.GenerateRandomBytes(requestId);
        int start = 0;
        while (start < requestId.Length && requestId[start] == 0)
        {
            start++;
        }

        return RequestId.From(requestId[start..]);
    }

    private bool TryGetSession(SessionKey sessionKey, [NotNullWhen(true)] out Session? session) => _sessions.TryGet(sessionKey, out session);

    private void SetSession(SessionKey sessionKey, Session session)
        => _sessions.Set(sessionKey, session);

    internal bool TryGetKnownNode(ValueHash256 nodeId, [NotNullWhen(true)] out Node? knownNode)
        => routingTable.TryGet(nodeId, out knownNode);

    internal static bool IsAcceptableNodeRecord(NodeRecord record, ValueHash256 expectedNodeId, PeerAddressPermissions allowNonRoutable)
        => TryGetAcceptableDiscoveryEndpoint(record, allowNonRoutable, listenerAddress: null, preferredEndpoint: null, out _) &&
            HasExpectedNodeId(record, expectedNodeId);

    internal static bool TryGetAcceptableNode(
        NodeRecord record,
        PeerAddressPermissions allowNonRoutable,
        IPAddress localIp,
        [NotNullWhen(true)] out Node? node)
        => TryGetAcceptableNodeCore(record, allowNonRoutable, localIp, preferredEndpoint: null, out node);

    /// <summary>
    /// Creates a node from a record received from <paramref name="sourceEndpoint"/>, only when the record advertises that endpoint.
    /// </summary>
    /// <remarks>
    /// A signed record proves the node identity, not the endpoint. A record from a handshake or an ENR refresh must
    /// advertise the endpoint that sent it, so that the peer cannot bind its identity to another address. The node is
    /// created only in the source's address family, so it has the source's normalized IP address bytes and UDP port; the
    /// IPv6 scope ID is not compared. When the source address itself is not acceptable, such as a special-use address,
    /// the record's endpoint in the other address family was never verified and is not used.
    /// </remarks>
    internal static bool TryGetAcceptableNode(
        NodeRecord record,
        PeerAddressPermissions allowNonRoutable,
        IPAddress localIp,
        IPEndPoint sourceEndpoint,
        [NotNullWhen(true)] out Node? node)
    {
        if (!HasDiscoveryEndpoint(record, sourceEndpoint) ||
            !TryGetAcceptableNodeCore(record, allowNonRoutable, localIp, sourceEndpoint, out node) ||
            DiscoveryAddressSupport.GetFamily(node.DiscoveryAddress.Address) != DiscoveryAddressSupport.GetFamily(sourceEndpoint.Address))
        {
            node = null;
            return false;
        }

        return true;
    }

    private static bool TryGetAcceptableNodeCore(
        NodeRecord record,
        PeerAddressPermissions allowNonRoutable,
        IPAddress localIp,
        IPEndPoint? preferredEndpoint,
        [NotNullWhen(true)] out Node? node)
    {
        node = null;
        if (!TryGetAcceptableDiscoveryEndpoint(record, allowNonRoutable, localIp, preferredEndpoint, out IPEndPoint? discoveryEndpoint))
        {
            return false;
        }

        PublicKey? key = record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress();
        if (key is null)
        {
            return false;
        }

        node = Node.FromDiscoveryEnr(record, key, discoveryEndpoint);
        return true;
    }

    internal static bool TryGetAcceptableDiscoveryEndpoint(
        NodeRecord record,
        PeerAddressPermissions allowNonRoutable,
        IPAddress? listenerAddress,
        IPEndPoint? preferredEndpoint,
        [NotNullWhen(true)] out IPEndPoint? endpoint)
    {
        Span<AddressFamily> addressFamilies = stackalloc AddressFamily[2];
        int count;
        if (listenerAddress is null)
        {
            addressFamilies[0] = AddressFamily.InterNetwork;
            addressFamilies[1] = AddressFamily.InterNetworkV6;
            count = 2;
        }
        else
        {
            count = DiscoveryAddressSupport.GetSupportedFamilies(listenerAddress, preferredEndpoint, addressFamilies);
        }

        for (int i = 0; i < count; i++)
        {
            if (record.TryGetDiscoveryEndpoint(addressFamilies[i], out endpoint) &&
                DiscoveryV5App.IsDiscoveryAddressAcceptable(endpoint.Address, allowNonRoutable))
            {
                return true;
            }
        }

        endpoint = null;
        return false;
    }

    internal static bool HasDiscoveryEndpoint(NodeRecord record, IPEndPoint endpoint)
    {
        IPAddress endpointAddress = endpoint.Address;
        AddressFamily family = DiscoveryAddressSupport.GetFamily(endpointAddress);
        IPAddress normalizedAddress = endpointAddress.NormalizeMappedIPv4();
        return record.TryGetDiscoveryEndpoint(family, out IPEndPoint? discoveryEndpoint) &&
               HasSameAddressBytes(discoveryEndpoint.Address, normalizedAddress) &&
               discoveryEndpoint.Port == endpoint.Port;
    }

    private void SetSentChallenge(ChallengeKey challengeKey, byte[] packet)
    {
        lock (_sentChallengesLock)
        {
            _sentChallenges.Set(challengeKey, new SentChallenge(packet, ChallengeTimeProvider.GetTimestamp()));
        }
    }

    private bool IsExpired(SentChallenge challenge)
        => ChallengeTimeProvider.GetElapsedTime(challenge.CreatedAtTimestamp) > SentChallengeTtl;

    internal bool TryAcceptChallenge(IPEndPoint endpoint)
        => (ChallengeLimiter ?? _challengeRateLimiter).TryAccept(endpoint.Address);

    private void StartEndpointCheck(Node remoteNode, CancellationToken token)
    {
        if (!_endpointChecks.TryReserveEndpoint(remoteNode, out IDisposable? lease))
        {
            return;
        }

        _ = RunEndpointCheck(remoteNode, lease!, token);
    }

    private async Task RunEndpointCheck(Node remoteNode, IDisposable lease, CancellationToken token)
    {
        try
        {
            ulong? sequence;
            try
            {
                sequence = await PingRoundTrip(remoteNode, token);
            }
            finally
            {
                lease.Dispose();
                if (Logger.IsTrace) Logger.Trace($"Discv5 endpoint check released for {remoteNode}.");
            }
            if (sequence is not null) await RefreshRemoteRecordIfNewer(remoteNode, sequence.Value, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (Logger.IsTrace) Logger.Trace($"Discv5 endpoint check failed for {remoteNode:hide}: {e:hide}");
        }
        finally
        {
            if (Logger.IsTrace) Logger.Trace($"Discv5 automatic endpoint cohort completed for {remoteNode}.");
        }
    }

    private void ReserveEndpointCheck(Node remoteNode) => _endpointChecks.Mark(remoteNode);

    internal bool TryReserveEndpointCheck(Node remoteNode) => _endpointChecks.TryReserve(remoteNode);

    internal void ReleaseEndpointCheck(Node remoteNode) => _endpointChecks.Release(remoteNode);

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowRequestNotSent() =>
        throw new KademliaRequestNotSentException();

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowTooManyDistances(int maxDistances, string paramName) =>
        throw new ArgumentException($"At most {maxDistances} distances may be requested.", paramName);
}
