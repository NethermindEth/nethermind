// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using Nethermind.Core.Caching;
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
public class KademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia,
    NettyDiscoveryV5Handler discoveryHandler,
    PacketCodec packetCodec,
    INodeRecordProvider nodeRecordProvider,
    IDiscoveryConfig discoveryConfig,
    ICryptoRandom cryptoRandom,
    IKademliaDistance<Hash256> distance,
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
    private const long SentChallengeTtlMilliseconds = 60_000;
    private const long EndpointCheckTtlMilliseconds = 60_000;
    private static readonly TimeSpan ChallengeRateLimitWindow = TimeSpan.FromMilliseconds(100);
    private const int ChallengeRateLimitBurstPerIp = 16;
    private const int ChallengeRateLimitFilterSize = 8_192;

    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _findNodeTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly IKademliaDistance<Hash256> _distance = distance;
    private readonly ILogger _logger = logManager.GetClassLogger<KademliaAdapter>();
    private readonly LruCache<SessionKey, Session> _sessions = new(MaxSessions, "discv5 sessions");
    private readonly LruCache<ChallengeKey, SentChallenge> _sentChallenges = new(MaxSentChallenges, "discv5 sent challenges");
    private readonly Queue<SentChallengeExpiry> _sentChallengeExpiries = new();
    private readonly object _sentChallengeExpiriesLock = new();
    private long _lastSentChallengeTrimMilliseconds;
    private readonly LruCache<PendingNonceKey, PendingRequest> _pendingByNonce = new(MaxPendingRequests, "discv5 pending requests");
    private readonly LruCache<ResponseKey, IResponseHandler> _responseHandlers = new(MaxResponseHandlers, "discv5 response handlers");
    private readonly LruCache<Hash256, NodeRecord> _knownRecords = new(MaxKnownRecords, "discv5 known records");
    private readonly LruCache<SessionKey, long> _endpointChecks = new(MaxEndpointChecks, "discv5 endpoint checks");
    private readonly NodeFilter[] _challengeRateLimiters = CreateChallengeRateLimiters();

    /// <inheritdoc/>
    public Node[] GetNodesAtDistances(IEnumerable<int> distances, Node? excluding = null)
    {
        ArgumentNullException.ThrowIfNull(distances);

        HashSet<Hash256> seen = [];
        List<Node> result = [];
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

        return [.. result];
    }

    /// <inheritdoc/>
    public async Task<bool> Ping(Node receiver, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        ReserveEndpointCheck(receiver);
        using PingMsg ping = new(CreateRequestId(), nodeRecordProvider.Current.EnrSequence);
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
        NodesResponseHandler responseHandler = new(receiver, distances, _distance);

        if (_logger.IsTrace) _logger.Trace($"Sending discv5 FINDNODE {findNode.RequestId} to {receiver:s}, distances: {FormatDistances(distances)}.");
        if (!await SendRequest(receiver, findNode, responseHandler, _findNodeTimeout, token))
        {
            if (_logger.IsTrace) _logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver:s} timed out.");
            return null;
        }

        Node[] nodes = responseHandler.GetNodes();
        if (_logger.IsTrace) _logger.Trace($"Discv5 FINDNODE {findNode.RequestId} to {receiver:s} returned {nodes.Length} nodes.");
        for (int i = 0; i < nodes.Length; i++)
        {
            kademlia.Value.AddOrRefresh(nodes[i]);
        }

        return nodes;
    }

    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            await foreach (PooledUdpReceiveResult result in discoveryHandler.ReadMessagesAsync(token))
            {
                await HandlePacket(result, token);
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
        ResponseKey responseKey = new(receiver.Id.Hash, request.RequestId, responseHandler.MessageType);
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
        SessionKey sessionKey = new(receiver.Id.Hash, receiver.Address);
        if (TryGetSession(sessionKey, out Session? session))
        {
            Span<byte> sessionNonce = stackalloc byte[PacketCodec.NonceSize];
            session.WriteNextNonce(cryptoRandom, sessionNonce);
            PendingNonceKey sessionPendingNonceKey = new(receiver.Address, NonceKey.From(sessionNonce));
            _pendingByNonce.Set(sessionPendingNonceKey, new PendingRequest(receiver, message));
            byte[] packet = packetCodec.EncodeOrdinary(receiver.Id, session.WriteKey, message, sessionNonce);
            try
            {
                if (_logger.IsTrace) _logger.Trace($"Sending discv5 ordinary {message.MessageType} {message.RequestId} to {receiver:s} with existing session, bytes: {packet.Length}.");
                await discoveryHandler.SendAsync(packet, receiver.Address);
                return sessionPendingNonceKey;
            }
            catch
            {
                _pendingByNonce.TryRemove(sessionPendingNonceKey, out _);
                throw;
            }
        }

        Span<byte> nonce = stackalloc byte[PacketCodec.NonceSize];
        cryptoRandom.GenerateRandomBytes(nonce);
        Span<byte> encryptionKey = stackalloc byte[16];
        cryptoRandom.GenerateRandomBytes(encryptionKey);
        PendingRequest pendingRequest = new(receiver, message);
        PendingNonceKey pendingNonceKey = new(receiver.Address, NonceKey.From(nonce));
        _pendingByNonce.Set(pendingNonceKey, pendingRequest);

        byte[] initialPacket = packetCodec.EncodeOrdinary(receiver.Id, encryptionKey, message, nonce);
        try
        {
            if (_logger.IsTrace) _logger.Trace($"Sending discv5 ordinary {message.MessageType} {message.RequestId} to {receiver:s} without session, bytes: {initialPacket.Length}.");
            await discoveryHandler.SendAsync(initialPacket, receiver.Address);
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
        SessionKey sessionKey = new(receiver.Id.Hash, receiver.Address);
        if (!TryGetSession(sessionKey, out Session? session))
        {
            return;
        }

        Span<byte> nonce = stackalloc byte[PacketCodec.NonceSize];
        session.WriteNextNonce(cryptoRandom, nonce);
        byte[] packet = packetCodec.EncodeOrdinary(receiver.Id, session.WriteKey, message, nonce);
        if (_logger.IsTrace) _logger.Trace($"Sending discv5 response {message.MessageType} {message.RequestId} to {receiver:s}, bytes: {packet.Length}.");
        await discoveryHandler.SendAsync(packet, receiver.Address);
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

        Challenge challenge = packetCodec.DecodeWhoAreYou(packet);
        byte[] handshakePacket = packetCodec.EncodeHandshake(pendingRequest.Receiver.Id, challenge, pendingRequest.Message, out Session session);
        SetSession(new SessionKey(pendingRequest.Receiver.Id.Hash, endpoint), session);
        if (_logger.IsTrace) _logger.Trace($"Sending discv5 HANDSHAKE for {pendingRequest.Message.MessageType} {pendingRequest.Message.RequestId} to {endpoint}, bytes: {handshakePacket.Length}, requested ENR seq: {challenge.EnrSequence}.");
        await discoveryHandler.SendAsync(handshakePacket, endpoint);
    }

    private async Task HandleOrdinary(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        if (!PacketCodec.TryGetSourceNodeId(packet, out Hash256? nodeId))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 ordinary packet from {endpoint}; source node id missing.");
            return;
        }

        SessionKey sessionKey = new(nodeId, endpoint);
        if (!TryGetSession(sessionKey, out Session? session) ||
            !packetCodec.TryDecryptMessage(packet, session.ReadKey, out Discv5Message message))
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

    private async Task HandleHandshake(IPEndPoint endpoint, Packet packet, CancellationToken token)
    {
        if (!PacketCodec.TryGetSourceNodeId(packet, out Hash256? nodeId))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake packet from {endpoint}; source node id missing.");
            return;
        }

        ChallengeKey challengeKey = new(nodeId, endpoint);
        if (!_sentChallenges.TryRemove(challengeKey, out SentChallenge sentChallenge) ||
            IsExpired(sentChallenge, Environment.TickCount64))
        {
            if (_logger.IsTrace) _logger.Trace($"Ignoring discv5 handshake packet from {endpoint}; matching challenge missing or expired.");
            return;
        }

        TryGetKnownRecord(nodeId, out NodeRecord? knownRecord);
        if (!packetCodec.TryDecryptHandshake(packet, sentChallenge.Challenge, knownRecord, out Session session, out Discv5Message message, out NodeRecord? nodeRecord))
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to decrypt discv5 handshake packet from {endpoint}.");
            return;
        }

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

                if (IsAcceptableNodeRecord(nodeRecord, nodeId, IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(endpoint.Address)))
                {
                    SetKnownRecord(nodeId, nodeRecord);
                    messageRecord = nodeRecord;
                }
            }

            SetSession(new SessionKey(nodeId, endpoint), session);
            if (_logger.IsTrace) _logger.Trace($"Received discv5 handshake message {message.MessageType} {message.RequestId} from {endpoint}, ENR included: {nodeRecord is not null}.");
            await HandleMessage(session.RemotePublicKey, endpoint, message, token, messageRecord);
        }
        finally
        {
            message.Dispose();
        }
    }

    private async Task SendWhoAreYou(IPEndPoint endpoint, Packet requestPacket, Hash256 nodeId)
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
        byte[] packet = packetCodec.EncodeWhoAreYou(nodeId.Bytes, requestPacket.Nonce.Span, enrSequence, out Challenge challenge);
        SetSentChallenge(challengeKey, challenge, packet);
        if (_logger.IsTrace) _logger.Trace($"Sending discv5 WHOAREYOU challenge to {endpoint}, known ENR seq: {enrSequence}, bytes: {packet.Length}.");
        await discoveryHandler.SendAsync(packet, endpoint);
    }

    private async Task HandleMessage(PublicKey remotePublicKey, IPEndPoint endpoint, Discv5Message message, CancellationToken token, NodeRecord? nodeRecord = null)
    {
        Node remoteNode = new(remotePublicKey, endpoint)
        {
            Enr = GetKnownEnr(remotePublicKey.Hash, nodeRecord)
        };
        if (HandleResponse(remotePublicKey.Hash, message))
        {
            if (_logger.IsTrace) _logger.Trace($"Handled discv5 response {message.MessageType} {message.RequestId} from {endpoint}.");
            kademlia.Value.AddOrRefresh(remoteNode);
            return;
        }

        if (_logger.IsTrace) _logger.Trace($"Handling discv5 request {message.MessageType} {message.RequestId} from {endpoint}.");
        switch (message)
        {
            case PingMsg ping:
                using (PongMsg pong = new(ping.RequestId, nodeRecordProvider.Current.EnrSequence, endpoint.Address, endpoint.Port))
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

    private string? GetKnownEnr(Hash256 nodeId, NodeRecord? nodeRecord)
    {
        if (nodeRecord is not null)
        {
            return nodeRecord.EnrString;
        }

        return _knownRecords.TryGet(nodeId, out NodeRecord? knownRecord) ? knownRecord.EnrString : null;
    }

    private bool HandleResponse(Hash256 nodeId, Discv5Message message)
    {
        ResponseKey responseKey = new(nodeId, message.RequestId, message.MessageType);
        return _responseHandlers.TryGet(responseKey, out IResponseHandler? handler) && handler.Handle(message);
    }

    private async Task HandleFindNode(Node remoteNode, FindNodeMsg findNode, CancellationToken token)
    {
        NodeRecord[] records = GetFindNodeRecords(findNode.Distances, remoteNode);
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

    private NodeRecord[] GetFindNodeRecords(Distances distances, Node requester)
    {
        HashSet<Hash256> seen = new(MaxFindNodeRecords);
        List<NodeRecord> result = new(MaxFindNodeRecords);
        bool allowNonRoutableRelays = IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(requester.Address.Address);
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
                    result.Add(nodeRecordProvider.Current);
                    includedSelf = true;
                }

                continue;
            }

            AddFindNodeRecordsAtDistance(distance, requester, allowNonRoutableRelays, seen, result);
        }

        return [.. result];
    }

    private void AddFindNodeRecordsAtDistance(
        int distance,
        Node requester,
        bool allowNonRoutableRelays,
        HashSet<Hash256> seen,
        List<NodeRecord> result)
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
            return IsAcceptableNodeRecord(knownRecord, node.Id.Hash, allowNonRoutableRelays) ? knownRecord : null;
        }

        try
        {
            NodeRecord record = NodeRecord.FromEnrString(node.Enr);
            return IsAcceptableNodeRecord(record, node.Id.Hash, allowNonRoutableRelays) ? record : null;
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
            if (IsAcceptableNodeRecord(record, node.Id.Hash, IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(node.Address.Address)))
            {
                SetKnownRecord(node.Id.Hash, record);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse known discv5 ENR for {node}: {e}");
        }
    }

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

    private bool TryGetKnownRecord(Hash256 nodeId, [NotNullWhen(true)] out NodeRecord? record) => _knownRecords.TryGet(nodeId, out record);

    private void SetKnownRecord(Hash256 nodeId, NodeRecord record)
        => _knownRecords.Set(nodeId, record);

    internal static bool IsAcceptableNodeRecord(NodeRecord record, Hash256 expectedNodeId, bool allowNonRoutable)
        => NodeRecordConverter.TryGetNodeFromEnr(record, allowNonRoutable, out Node? node) &&
            node.Id.Hash.Equals(expectedNodeId);

    internal static bool HasExpectedNodeId(NodeRecord record, Hash256 expectedNodeId)
        => record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Hash.Equals(expectedNodeId) == true;

    private void SetSentChallenge(ChallengeKey challengeKey, Challenge challenge, byte[] packet)
    {
        long now = Environment.TickCount64;
        TryTrimExpiredChallenges(now);
        _sentChallenges.Set(challengeKey, new SentChallenge(challenge, packet, now));
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
    {
        for (int i = 0; i < _challengeRateLimiters.Length; i++)
        {
            if (_challengeRateLimiters[i].TryAccept(endpoint.Address, exactOnly: true))
            {
                return true;
            }
        }

        return false;
    }

    private static NodeFilter[] CreateChallengeRateLimiters()
    {
        NodeFilter[] filters = new NodeFilter[ChallengeRateLimitBurstPerIp];
        for (int i = 0; i < filters.Length; i++)
        {
            filters[i] = NodeFilter.CreateExact(ChallengeRateLimitFilterSize, ChallengeRateLimitWindow);
        }

        return filters;
    }

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
        => _endpointChecks.Set(new SessionKey(remoteNode.Id.Hash, remoteNode.Address), Environment.TickCount64);

    private bool TryReserveEndpointCheck(Node remoteNode)
    {
        SessionKey sessionKey = new(remoteNode.Id.Hash, remoteNode.Address);
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
