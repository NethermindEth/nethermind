// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Runtime.CompilerServices;
using Collections.Pooled;
using Nethermind.Core.Caching;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
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
    NettyDiscoveryV5Handler discoveryHandler,
    PacketCodec packetCodec,
    INodeRecordProvider nodeRecordProvider,
    IDiscoveryConfig discoveryConfig,
    ICryptoRandom cryptoRandom,
    IKademliaDistance<Hash256> distance,
    IDiscv5RecordFilter recordFilter,
    ILogManager logManager) : IKademliaAdapter
{
    private const int MaxFindNodeRecords = 16;
    private const int MaxEnrsPerNodesMessage = 3;
    private const int MaxSessions = 4_096;
    private const int MaxSentChallenges = 4_096;
    private const int MaxPendingRequests = 4_096;
    private const int MaxResponseHandlers = 1_024;
    private const int MaxKnownRecords = 16_384;
    private const int MaxEndpointChecks = 4_096;
    private const int PacketWorkerCount = 4;
    private const long SentChallengeTtlMilliseconds = 60_000;
    private const long EndpointCheckTtlMilliseconds = 60_000;
    private static readonly TimeSpan ChallengeRateLimitWindow = TimeSpan.FromMilliseconds(100);
    private const int ChallengeRateLimitBurstPerIp = 16;
    private const int ChallengeRateLimitFilterSize = 8_192;

    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _findNodeTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly IKademliaDistance<Hash256> _distance = distance;
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaAdapter>();
    private readonly DisposingLruCache<SessionKey, Session> _sessions = new(MaxSessions, "discv5 sessions");
    private readonly LruCache<ChallengeKey, SentChallenge> _sentChallenges = new(MaxSentChallenges, "discv5 sent challenges");
    private readonly Queue<SentChallengeExpiry> _sentChallengeExpiries = new();
    private readonly Lock _sentChallengeExpiriesLock = new();
    private long _lastSentChallengeTrimMilliseconds;
    private readonly LruCache<PendingNonceKey, PendingRequest> _pendingByNonce = new(MaxPendingRequests, "discv5 pending requests");
    private readonly LruCache<ResponseKey, IResponseHandler> _responseHandlers = new(MaxResponseHandlers, "discv5 response handlers");
    private readonly LruCache<ValueHash256, NodeRecord> _knownRecords = new(MaxKnownRecords, "discv5 known records");
    private readonly Lock _knownRecordsLock = new();
    private readonly LruCache<SessionKey, long> _endpointChecks = new(MaxEndpointChecks, "discv5 endpoint checks");
    private readonly AddressBurstLimiter _challengeRateLimiter = new(ChallengeRateLimitBurstPerIp, ChallengeRateLimitFilterSize, ChallengeRateLimitWindow);

    /// <inheritdoc/>
    public Node[] GetNodesAtDistances(IEnumerable<int> distances, Node? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(distances);

        using PooledSet<Hash256> seen = new(MaxFindNodeRecords);
        using ArrayPoolListRef<Node> result = new(MaxFindNodeRecords);
        Hash256? excludedHash = excluding?.IdHash;

        foreach (int distance in distances)
        {
            if (distance < 0 || distance > _distance.MaxDistance)
            {
                throw new ArgumentOutOfRangeException(nameof(distances), distance, $"Distance must be between 0 and {_distance.MaxDistance}.");
            }

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
                }
            }
        }

        return result.Count == 0 ? [] : result.ToArray();
    }

    /// <inheritdoc/>
    public async Task<bool> Ping(Node receiver, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        ReserveEndpointCheck(receiver);
        using PingMsg ping = new(CreateRequestId(), (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence);
        PongResponseHandler responseHandler = new(receiver);

        if (_logger.IsTrace) _logger.Trace($"Sending discv5 PING {ping.RequestId} to {receiver:s}.");
        if (!await SendRequest(receiver, ping, responseHandler, _pingTimeout, token))
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 PING {ping.RequestId} to {receiver:s} timed out.");
            return false;
        }

        if (_logger.IsTrace) _logger.Trace($"Discv5 PING {ping.RequestId} to {receiver:s} succeeded.");
        kademlia.Value.AddOrRefresh(receiver);
        return true;
    }

    /// <inheritdoc/>
    public async Task<Node[]?> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        Distances distances = GetLookupDistances(receiver, target);
        using FindNodeMsg findNode = new(CreateRequestId(), distances);
        using NodesResponseHandler responseHandler = new(receiver, distances, _distance, recordFilter);

        if (_logger.IsTrace) _logger.Trace($"Sending discv5 FINDNODE {findNode.RequestId} to {receiver:s}, distances: {FormatDistances(distances)}.");
        if (!await SendRequest(receiver, findNode, responseHandler, _findNodeTimeout, token))
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver:s} timed out.");
            return null;
        }

        Node[] nodes = responseHandler.GetNodes();
        for (int i = 0; i < nodes.Length; i++)
        {
            kademlia.Value.AddOrRefresh(nodes[i]);
        }

        if (_logger.IsTrace) _logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver:s} returned {nodes.Length} nodes.");
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
            await foreach (PooledUdpReceiveResult result in discoveryHandler.ReadMessagesAsync(token))
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
                    if (_logger.IsTrace) _logger.Trace($"Error handling discv5 packet from {result.RemoteEndPoint}: {e}");
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
            if (_logger.IsError) _logger.Error("Error in discv5 packet loop", e);
        }
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private async Task<bool> SendRequest<TResponse>(
        Node receiver,
        Discv5Message request,
        IResponseHandler<TResponse> responseHandler,
        TimeSpan timeout,
        CancellationToken token)
        where TResponse : Discv5Message
    {
        ResponseKey responseKey = new(receiver.Id.Hash.ValueHash256, request.RequestId, responseHandler.MessageType);
        _responseHandlers.Set(responseKey, responseHandler);

        using CancellationTokenSource timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeoutCts.CancelAfter(timeout);

        PendingNonceKey? pendingNonceKey = null;
        try
        {
            pendingNonceKey = await SendMessage(receiver, request);
            await responseHandler.Task.WaitAsync(timeoutCts.Token);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 request {request.MessageType} {request.RequestId} to {receiver:s} timed out after {timeout}.");
            return false;
        }
        finally
        {
            _responseHandlers.TryRemove(responseKey, out _);
            if (pendingNonceKey is not null)
            {
                _pendingByNonce.TryRemove(pendingNonceKey.Value, out _);
            }
        }
    }

    private async Task<PendingNonceKey?> SendMessage(Node receiver, Discv5Message message)
    {
        if (TryEncodeWithExistingSession(receiver, message, out PendingNonceKey pendingNonceKey, out byte[]? packet))
        {
            return await SendPendingPacket(receiver, message, pendingNonceKey, packet, hasSession: true);
        }

        pendingNonceKey = EncodeMessageWithoutSession(receiver, message, out byte[] initialPacket);
        return await SendPendingPacket(receiver, message, pendingNonceKey, initialPacket, hasSession: false);
    }

    [SkipLocalsInit]
    private bool TryEncodeWithExistingSession(
        Node receiver,
        Discv5Message message,
        out PendingNonceKey pendingNonceKey,
        [NotNullWhen(true)] out byte[]? packet)
    {
        SessionKey sessionKey = new(receiver.Id.Hash.ValueHash256, receiver.Address);
        if (TryGetSession(sessionKey, out Session? session))
        {
            Span<byte> writeKey = stackalloc byte[Session.KeySize];
            if (session.TryCopyWriteKey(writeKey))
            {
                Span<byte> sessionNonce = stackalloc byte[PacketCodec.NonceSize];
                session.WriteNextNonce(cryptoRandom, sessionNonce);
                pendingNonceKey = new PendingNonceKey(receiver.Address, NonceKey.From(sessionNonce));
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
        PendingNonceKey pendingNonceKey = new(receiver.Address, NonceKey.From(nonce));
        initialPacket = packetCodec.EncodeOrdinary(receiver.Id, encryptionKey, message, nonce);
        return pendingNonceKey;
    }

    private async Task<PendingNonceKey> SendPendingPacket(
        Node receiver,
        Discv5Message message,
        PendingNonceKey pendingNonceKey,
        byte[] packet,
        bool hasSession)
    {
        _pendingByNonce.Set(pendingNonceKey, new PendingRequest(receiver, message));
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending discv5 ordinary {message.MessageType} {message.RequestId} to {receiver:s} {(hasSession ? "with existing session" : "without session")}, bytes: {packet.Length}.");
            await discoveryHandler.SendAsync(packet, receiver.Address);
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

        if (_logger.IsTrace) _logger.Trace($"Sending discv5 response {message.MessageType} {message.RequestId} to {receiver:s}, bytes: {packet.Length}.");
        await discoveryHandler.SendAsync(packet, receiver.Address);
    }

    [SkipLocalsInit]
    private bool TryEncodeResponse(Node receiver, Discv5Message message, [NotNullWhen(true)] out byte[]? packet)
    {
        SessionKey sessionKey = new(receiver.Id.Hash.ValueHash256, receiver.Address);
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
        return true;
    }

    private async Task HandlePacket(PooledUdpReceiveResult udpPacket, CancellationToken token)
    {
        if (!packetCodec.TryDecode(udpPacket.Buffer, out Packet packet))
        {
            if (_logger.IsTrace) _logger.Trace($"Dropping undecodable discv5 packet from {udpPacket.RemoteEndPoint}, bytes: {udpPacket.Buffer.Length}.");
            return;
        }

        using (packet)
        {
            if (_logger.IsTrace) _logger.Trace($"Received discv5 {packet.Flag} packet from {udpPacket.RemoteEndPoint}, bytes: {udpPacket.Buffer.Length}.");
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
                if (_logger.IsDebug) _logger.Debug($"Error handling discv5 packet from {udpPacket.RemoteEndPoint}: {e}");
            }
        }
    }

    private async Task HandleWhoAreYou(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        PendingNonceKey pendingNonceKey = new(endpoint, NonceKey.From(packet.Nonce.Span));
        if (!_pendingByNonce.TryRemove(pendingNonceKey, out PendingRequest? pendingRequest))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 WHOAREYOU from {endpoint}; no pending request for nonce.");
            return;
        }

        byte[] handshakePacket;
        Session session;
        ulong requestedEnrSequence;
        using (Challenge challenge = packetCodec.DecodeWhoAreYou(in packet))
        {
            handshakePacket = packetCodec.EncodeHandshake(pendingRequest.Receiver.Id, challenge, pendingRequest.Message, out session);
            requestedEnrSequence = challenge.EnrSequence;
        }

        SetSession(new SessionKey(pendingRequest.Receiver.Id.Hash.ValueHash256, endpoint), session);
        if (_logger.IsTrace) _logger.Trace($"Sending discv5 HANDSHAKE for {pendingRequest.Message.MessageType} {pendingRequest.Message.RequestId} to {endpoint}, bytes: {handshakePacket.Length}, requested ENR seq: {requestedEnrSequence}.");
        await discoveryHandler.SendAsync(handshakePacket, endpoint);
    }

    private async Task HandleOrdinary(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        if (!PacketCodec.TryGetSourceNodeId(in packet, out ValueHash256 nodeId))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 ordinary packet from {endpoint}; source node id missing.");
            return;
        }

        SessionKey sessionKey = new(nodeId, endpoint);
        if (!TryDecryptOrdinaryMessage(in packet, sessionKey, out Session? session, out Discv5Message? message))
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 ordinary packet from {endpoint} could not be decrypted with an existing session; sending WHOAREYOU.");
            await SendWhoAreYou(endpoint, packet, nodeId);
            return;
        }

        try
        {
            if (_logger.IsTrace) _logger.Trace($"Received discv5 message {message.MessageType} {message.RequestId} from {endpoint}.");
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
            packetCodec.TryDecryptMessage(in packet, readKey, out Discv5Message decodedMessage))
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
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake packet from {endpoint}; source node id missing.");
            return;
        }

        ChallengeKey challengeKey = new(nodeId, endpoint);
        if (!_sentChallenges.TryRemove(challengeKey, out SentChallenge sentChallenge))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake packet from {endpoint}; matching challenge missing.");
            return;
        }

        if (IsExpired(sentChallenge, Environment.TickCount64))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake packet from {endpoint}; matching challenge expired.");
            return;
        }

        TryGetKnownRecord(nodeId, out NodeRecord? knownRecord);
        if (!PacketCodec.TryDecode(sentChallenge.Packet, nodeId.Bytes, out Packet challengePacket))
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to decode matching discv5 WHOAREYOU challenge for {endpoint}.");
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
                if (_logger.IsTrace) _logger.Trace($"Unable to decrypt discv5 handshake packet from {endpoint}.");
                return;
            }
        }

        await HandleHandshakeMessage(endpoint, nodeId, session, message, nodeRecord, knownRecord, token);
    }

    private async Task SendWhoAreYou(IPEndPoint endpoint, Packet requestPacket, ValueHash256 nodeId)
    {
        ChallengeKey challengeKey = new(nodeId, endpoint);
        long now = Environment.TickCount64;
        if (_sentChallenges.TryGet(challengeKey, out SentChallenge existingChallenge) && !IsExpired(existingChallenge, now))
        {
            if (_logger.IsTrace) _logger.Trace($"Resending discv5 WHOAREYOU challenge to {endpoint}.");
            await discoveryHandler.SendAsync(existingChallenge.Packet, endpoint);
            return;
        }

        if (!TryAcceptChallenge(endpoint))
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discv5 WHOAREYOU challenge to {endpoint}.");
            return;
        }

        ulong enrSequence = TryGetKnownRecord(nodeId, out NodeRecord? record) ? record.EnrSequence : 0UL;
        byte[] packet = packetCodec.EncodeWhoAreYou(nodeId.Bytes, requestPacket.Nonce.Span, enrSequence);
        SetSentChallenge(challengeKey, packet);
        if (_logger.IsTrace) _logger.Trace($"Sending discv5 WHOAREYOU challenge to {endpoint}, known ENR seq: {enrSequence}, bytes: {packet.Length}.");
        await discoveryHandler.SendAsync(packet, endpoint);
    }

    private async Task HandleHandshakeMessage(
        IPEndPoint endpoint,
        ValueHash256 nodeId,
        Session session,
        Discv5Message message,
        NodeRecord? nodeRecord,
        NodeRecord? knownRecord,
        CancellationToken token)
    {
        bool sessionStored = false;
        try
        {
            NodeRecord? messageRecord = knownRecord;
            if (nodeRecord is not null)
            {
                if (!HasExpectedNodeId(nodeRecord, nodeId))
                {
                    if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake ENR from {endpoint}; ENR node id does not match packet source.");
                    return;
                }

                if (IsAcceptableNodeRecord(nodeRecord, nodeId, endpoint.Address.IsLoopbackOrPrivateOrLinkLocal, recordFilter))
                {
                    TrySetKnownRecord(nodeId, nodeRecord, out NodeRecord currentRecord);
                    messageRecord = currentRecord;
                }
            }

            SetSession(new SessionKey(nodeId, endpoint), session);
            sessionStored = true;
            if (_logger.IsTrace) _logger.Trace($"Received discv5 handshake message {message.MessageType} {message.RequestId} from {endpoint}, ENR included: {nodeRecord is not null}.");
            await HandleMessage(session.RemotePublicKey, endpoint, message, token, messageRecord);
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

    private async Task HandleMessage(PublicKey remotePublicKey, IPEndPoint endpoint, Discv5Message message, CancellationToken token, NodeRecord? nodeRecord = null)
    {
        ValueHash256 remoteNodeId = remotePublicKey.Hash.ValueHash256;
        Node remoteNode = new(remotePublicKey, endpoint)
        {
            Enr = GetKnownEnr(remoteNodeId, nodeRecord)
        };
        if (HandleResponse(remoteNodeId, message))
        {
            if (_logger.IsTrace) _logger.Trace($"Handled discv5 response {message.MessageType} {message.RequestId} from {endpoint}.");
            kademlia.Value.AddOrRefresh(remoteNode);
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"Handling discv5 request {message.MessageType} {message.RequestId} from {endpoint}.");
        switch (message)
        {
            case PingMsg ping:
                using (PongMsg pong = new(ping.RequestId, (await nodeRecordProvider.GetCurrentAsync(token)).EnrSequence, endpoint.Address, endpoint.Port))
                {
                    await SendResponse(remoteNode, pong, token);
                }

                kademlia.Value.AddOrRefresh(remoteNode);
                if (!string.IsNullOrEmpty(remoteNode.Enr))
                {
                    StartEndpointCheck(remoteNode, token);
                }
                break;
            case FindNodeMsg findNode:
                await HandleFindNode(remoteNode, findNode, token);
                kademlia.Value.AddOrRefresh(remoteNode);
                break;
            case TalkReqMsg talkReq:
                using (TalkRespMsg talkResp = new(talkReq.RequestId, ReadOnlyMemory<byte>.Empty))
                {
                    await SendResponse(remoteNode, talkResp, token);
                }

                break;
        }
    }

    private string? GetKnownEnr(ValueHash256 nodeId, NodeRecord? nodeRecord)
        => nodeRecord?.EnrString ?? (_knownRecords.TryGet(nodeId, out NodeRecord? knownRecord) ? knownRecord.EnrString : null);

    private bool HandleResponse(ValueHash256 nodeId, Discv5Message message)
    {
        ResponseKey responseKey = new(nodeId, message.RequestId, message.MessageType);
        return _responseHandlers.TryGet(responseKey, out IResponseHandler? handler) && handler.Handle(message);
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
            bool allowNonRoutableRelays = requester.Address.Address.IsLoopbackOrPrivateOrLinkLocal;
            bool includedSelf = false;
            for (int i = 0; i < distances.Count && result.Count < MaxFindNodeRecords; i++)
            {
                int distance = distances[i];
                if (distance < 0 || distance > _distance.MaxDistance)
                {
                    continue;
                }

                if (distance == 0)
                {
                    if (!includedSelf)
                    {
                        result.Add(selfRecord);
                        includedSelf = true;
                    }

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
        bool allowNonRoutableRelays,
        PooledSet<Hash256> seen,
        ref ArrayPoolListRef<NodeRecord> result)
    {
        Node[] nodes = kademlia.Value.GetAllAtDistance(distance);
        Hash256 requesterHash = requester.IdHash;
        for (int i = 0; i < nodes.Length && result.Count < MaxFindNodeRecords; i++)
        {
            Node node = nodes[i];

            if (node.IdHash.Equals(requesterHash) || string.IsNullOrEmpty(node.Enr) || !seen.Add(node.Id.Hash))
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

    private NodeRecord? GetFindNodeRecord(Node node, bool allowNonRoutableRelays)
    {
        if (TryGetKnownRecord(node.Id.Hash, out NodeRecord? knownRecord))
        {
            return IsAcceptableNodeRecord(knownRecord, node.Id.Hash, allowNonRoutableRelays, recordFilter) ? knownRecord : null;
        }

        try
        {
            NodeRecord record = NodeRecord.FromEnrString(node.Enr);
            return IsAcceptableNodeRecord(record, node.Id.Hash, allowNonRoutableRelays, recordFilter) ? record : null;
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse discv5 FINDNODE ENR for {node}: {e}");
            return null;
        }
    }

    private void RegisterKnownRecord(Node node)
    {
        if (string.IsNullOrEmpty(node.Enr))
        {
            return;
        }

        try
        {
            NodeRecord record = NodeRecord.FromEnrString(node.Enr);
            if (IsAcceptableNodeRecord(record, node.Id.Hash, node.Address.Address.IsLoopbackOrPrivateOrLinkLocal, recordFilter))
            {
                TrySetKnownRecord(node.Id.Hash, record, out _);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse known discv5 ENR for {node}: {e}");
        }
    }

    [SkipLocalsInit]
    internal Distances GetLookupDistances(Node receiver, PublicKey target)
    {
        int distance = _distance.CalculateLogDistance(receiver.Id.Hash, target.Hash);

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

    private bool TryGetKnownRecord(ValueHash256 nodeId, [NotNullWhen(true)] out NodeRecord? record) => _knownRecords.TryGet(nodeId, out record);

    internal bool TrySetKnownRecord(ValueHash256 nodeId, NodeRecord record, out NodeRecord currentRecord)
    {
        lock (_knownRecordsLock)
        {
            if (_knownRecords.TryGet(nodeId, out NodeRecord? knownRecord) && knownRecord.EnrSequence >= record.EnrSequence)
            {
                currentRecord = knownRecord;
                return false;
            }

            _knownRecords.Set(nodeId, record);
            currentRecord = record;
            return true;
        }
    }

    internal static bool IsAcceptableNodeRecord(NodeRecord record, ValueHash256 expectedNodeId, bool allowNonRoutable, IDiscv5RecordFilter recordFilter)
        => !recordFilter.Excludes(record) &&
            Node.TryFromDiscoveryEnr(record, out Node? node) &&
            node.Id.Hash == expectedNodeId &&
            DiscoveryV5App.IsDiscoveryAddressAcceptable(node.Address.Address, allowNonRoutable);

    internal static bool HasExpectedNodeId(NodeRecord record, ValueHash256 expectedNodeId)
        => record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Hash == expectedNodeId;

    private void SetSentChallenge(ChallengeKey challengeKey, byte[] packet)
    {
        long now = Environment.TickCount64;
        TryTrimExpiredChallenges(now);
        _sentChallenges.Set(challengeKey, new SentChallenge(packet, now));
        lock (_sentChallengeExpiriesLock)
        {
            _sentChallengeExpiries.Enqueue(new SentChallengeExpiry(challengeKey, now));
        }
    }

    private void TryTrimExpiredChallenges(long now)
    {
        long lastTrim = Volatile.Read(ref _lastSentChallengeTrimMilliseconds);
        if (now - lastTrim <= SentChallengeTtlMilliseconds ||
            Interlocked.CompareExchange(ref _lastSentChallengeTrimMilliseconds, now, lastTrim) != lastTrim)
        {
            return;
        }

        TrimExpiredChallenges(now);
    }

    private void TrimExpiredChallenges(long now)
    {
        lock (_sentChallengeExpiriesLock)
        {
            while (_sentChallengeExpiries.TryPeek(out SentChallengeExpiry expiry) &&
                   now - expiry.CreatedAtMilliseconds > SentChallengeTtlMilliseconds)
            {
                _sentChallengeExpiries.Dequeue();
                if (_sentChallenges.TryGet(expiry.Key, out SentChallenge challenge) &&
                    challenge.CreatedAtMilliseconds == expiry.CreatedAtMilliseconds)
                {
                    _sentChallenges.TryRemove(expiry.Key, out _);
                }
            }
        }
    }

    private static bool IsExpired(SentChallenge challenge, long now)
        => now - challenge.CreatedAtMilliseconds > SentChallengeTtlMilliseconds;

    internal bool TryAcceptChallenge(IPEndPoint endpoint)
        => _challengeRateLimiter.TryAccept(endpoint.Address);

    private void StartEndpointCheck(Node remoteNode, CancellationToken token)
    {
        if (!TryReserveEndpointCheck(remoteNode))
        {
            return;
        }

        _ = RunEndpointCheck(remoteNode, token);
    }

    private async Task RunEndpointCheck(Node remoteNode, CancellationToken token)
    {
        try
        {
            _ = await Ping(remoteNode, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 endpoint check failed for {remoteNode}: {e}");
        }
    }

    private void ReserveEndpointCheck(Node remoteNode)
        => _endpointChecks.Set(new SessionKey(remoteNode.Id.Hash.ValueHash256, remoteNode.Address), Environment.TickCount64);

    private bool TryReserveEndpointCheck(Node remoteNode)
    {
        SessionKey sessionKey = new(remoteNode.Id.Hash.ValueHash256, remoteNode.Address);
        long now = Environment.TickCount64;
        if (_endpointChecks.TryGet(sessionKey, out long startedAt) &&
            now - startedAt <= EndpointCheckTtlMilliseconds)
        {
            return false;
        }

        _endpointChecks.Set(sessionKey, now);
        return true;
    }
}
