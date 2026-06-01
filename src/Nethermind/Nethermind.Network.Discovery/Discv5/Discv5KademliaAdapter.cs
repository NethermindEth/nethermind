// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Network.Discovery.Discv5.Handlers;
using Nethermind.Logging;
using Nethermind.Network.Discovery.Discv5.Messages;
using Nethermind.Network.Enr;
using Nethermind.Stats.Model;

namespace Nethermind.Network.Discovery.Discv5;

/// <summary>
/// Maps discv5 FINDNODE distance requests onto the protocol-specific Kademlia table.
/// </summary>
public class Discv5KademliaAdapter(
    Lazy<IKademlia<PublicKey, Node>> kademlia,
    NettyDiscoveryV5Handler discoveryHandler,
    Discv5PacketCodec packetCodec,
    INodeRecordProvider nodeRecordProvider,
    IDiscoveryConfig discoveryConfig,
    ICryptoRandom cryptoRandom,
    IKademliaDistance<Hash256> distance,
    ILogManager logManager) : IDiscv5KademliaAdapter
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
    private readonly ILogger _logger = logManager.GetClassLogger<Discv5KademliaAdapter>();
    private readonly LruCache<SessionKey, Discv5Session> _sessions = new(MaxSessions, "discv5 sessions");
    private readonly LruCache<ChallengeKey, SentChallenge> _sentChallenges = new(MaxSentChallenges, "discv5 sent challenges");
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
    public async Task Ping(Node receiver, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        ReserveEndpointCheck(receiver);
        using Discv5Ping ping = new(CreateRequestId(), nodeRecordProvider.Current.EnrSequence);
        PongResponseHandler responseHandler = new(receiver);

        await SendRequest(receiver, ping, responseHandler, _pingTimeout, token);
        kademlia.Value.AddOrRefresh(receiver);
    }

    /// <inheritdoc/>
    public async Task<Node[]> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        Discv5Distances distances = GetLookupDistances(receiver, target);
        using Discv5FindNode findNode = new(CreateRequestId(), distances);
        NodesResponseHandler responseHandler = new(receiver, distances, _distance);

        await SendRequest(receiver, findNode, responseHandler, _findNodeTimeout, token);
        Node[] nodes = responseHandler.GetNodes();
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
            await foreach (UdpReceiveResult result in discoveryHandler.ReadMessagesAsync(token))
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

    private async Task SendRequest<TResponse>(
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
        }
        catch (OperationCanceledException)
        {
            throw;
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
        if (TryGetSession(sessionKey, out Discv5Session? session))
        {
            byte[] sessionNonce = session.GetNextNonce(cryptoRandom);
            PendingNonceKey sessionPendingNonceKey = new(receiver.Address, NonceKey.From(sessionNonce));
            _pendingByNonce.Set(sessionPendingNonceKey, new PendingRequest(receiver, message));
            byte[] packet = packetCodec.EncodeOrdinary(receiver.Id, session.WriteKey, message, sessionNonce);
            try
            {
                await discoveryHandler.SendAsync(packet, receiver.Address);
                return sessionPendingNonceKey;
            }
            catch
            {
                _pendingByNonce.TryRemove(sessionPendingNonceKey, out _);
                throw;
            }
        }

        byte[] nonce = cryptoRandom.GenerateRandomBytes(Discv5PacketCodec.NonceSize);
        byte[] encryptionKey = cryptoRandom.GenerateRandomBytes(16);
        PendingRequest pendingRequest = new(receiver, message);
        PendingNonceKey pendingNonceKey = new(receiver.Address, NonceKey.From(nonce));
        _pendingByNonce.Set(pendingNonceKey, pendingRequest);

        byte[] initialPacket = packetCodec.EncodeOrdinary(receiver.Id, encryptionKey, message, nonce);
        try
        {
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
        if (!TryGetSession(sessionKey, out Discv5Session? session))
        {
            return;
        }

        byte[] packet = packetCodec.EncodeOrdinary(receiver.Id, session.WriteKey, message, session.GetNextNonce(cryptoRandom));
        await discoveryHandler.SendAsync(packet, receiver.Address);
    }

    private async Task HandlePacket(UdpReceiveResult udpPacket, CancellationToken token)
    {
        if (!packetCodec.TryDecode(udpPacket.Buffer, out Discv5Packet packet))
        {
            return;
        }

        try
        {
            switch (packet.Flag)
            {
                case Discv5PacketFlag.WhoAreYou:
                    await HandleWhoAreYou(udpPacket.RemoteEndPoint, packet, token);
                    break;
                case Discv5PacketFlag.Ordinary:
                    await HandleOrdinary(udpPacket.RemoteEndPoint, packet, token);
                    break;
                case Discv5PacketFlag.Handshake:
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

    private async Task HandleWhoAreYou(IPEndPoint endpoint, Discv5Packet packet, CancellationToken token)
    {
        PendingNonceKey pendingNonceKey = new(endpoint, NonceKey.From(packet.Nonce));
        if (!_pendingByNonce.TryRemove(pendingNonceKey, out PendingRequest? pendingRequest))
        {
            return;
        }

        Discv5Challenge challenge = packetCodec.DecodeWhoAreYou(packet);
        byte[] handshakePacket = packetCodec.EncodeHandshake(pendingRequest.Receiver.Id, challenge, pendingRequest.Message, out Discv5Session session);
        SetSession(new SessionKey(pendingRequest.Receiver.Id.Hash, endpoint), session);
        await discoveryHandler.SendAsync(handshakePacket, endpoint);
    }

    private async Task HandleOrdinary(IPEndPoint endpoint, Discv5Packet packet, CancellationToken token)
    {
        if (!Discv5PacketCodec.TryGetSourceNodeId(packet, out byte[] sourceNodeId))
        {
            return;
        }

        Hash256 nodeId = new(sourceNodeId);
        SessionKey sessionKey = new(nodeId, endpoint);
        if (!TryGetSession(sessionKey, out Discv5Session? session) ||
            !packetCodec.TryDecryptMessage(packet, session.ReadKey, out Discv5Message message))
        {
            await SendWhoAreYou(endpoint, packet, sourceNodeId);
            return;
        }

        try
        {
            await HandleMessage(session.RemotePublicKey, endpoint, message, token);
        }
        finally
        {
            message.Dispose();
        }
    }

    private async Task HandleHandshake(IPEndPoint endpoint, Discv5Packet packet, CancellationToken token)
    {
        if (!Discv5PacketCodec.TryGetSourceNodeId(packet, out byte[] sourceNodeId))
        {
            return;
        }

        Hash256 nodeId = new(sourceNodeId);
        ChallengeKey challengeKey = new(nodeId, endpoint);
        if (!_sentChallenges.TryRemove(challengeKey, out SentChallenge sentChallenge) ||
            IsExpired(sentChallenge, Environment.TickCount64))
        {
            return;
        }

        TryGetKnownRecord(nodeId, out NodeRecord? knownRecord);
        if (!packetCodec.TryDecryptHandshake(packet, sentChallenge.Challenge, knownRecord, out Discv5Session session, out Discv5Message message, out NodeRecord? nodeRecord))
        {
            return;
        }

        NodeRecord? messageRecord = knownRecord;
        if (nodeRecord is not null)
        {
            if (!HasExpectedNodeId(nodeRecord, nodeId))
            {
                return;
            }

            if (IsAcceptableNodeRecord(nodeRecord, nodeId, IPAddressClassifier.IsLoopbackOrPrivateOrLinkLocal(endpoint.Address)))
            {
                SetKnownRecord(nodeId, nodeRecord);
                messageRecord = nodeRecord;
            }
        }

        SetSession(new SessionKey(nodeId, endpoint), session);
        try
        {
            await HandleMessage(session.RemotePublicKey, endpoint, message, token, messageRecord);
        }
        finally
        {
            message.Dispose();
        }
    }

    private async Task SendWhoAreYou(IPEndPoint endpoint, Discv5Packet requestPacket, byte[] destinationNodeId)
    {
        Hash256 nodeId = new(destinationNodeId);
        ChallengeKey challengeKey = new(nodeId, endpoint);
        long now = Environment.TickCount64;
        if (_sentChallenges.TryGet(challengeKey, out SentChallenge existingChallenge) && !IsExpired(existingChallenge, now))
        {
            await discoveryHandler.SendAsync(existingChallenge.Packet, endpoint);
            return;
        }

        if (!TryAcceptChallenge(endpoint))
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discv5 WHOAREYOU challenge to {endpoint}.");
            return;
        }

        ulong enrSequence = TryGetKnownRecord(nodeId, out NodeRecord? record) ? record.EnrSequence : 0UL;
        byte[] packet = packetCodec.EncodeWhoAreYou(destinationNodeId, requestPacket.Nonce, enrSequence, out Discv5Challenge challenge);
        SetSentChallenge(challengeKey, challenge, packet);
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
            kademlia.Value.AddOrRefresh(remoteNode);
            return;
        }

        switch (message)
        {
            case Discv5Ping ping:
                using (Discv5Pong pong = new(ping.RequestId, nodeRecordProvider.Current.EnrSequence, endpoint.Address, endpoint.Port))
                {
                    await SendResponse(remoteNode, pong, token);
                }

                kademlia.Value.AddOrRefresh(remoteNode);
                if (!string.IsNullOrEmpty(remoteNode.Enr))
                {
                    StartEndpointCheck(remoteNode, token);
                }
                break;
            case Discv5FindNode findNode:
                await HandleFindNode(remoteNode, findNode, token);
                kademlia.Value.AddOrRefresh(remoteNode);
                break;
            case Discv5TalkReq talkReq:
                using (Discv5TalkResp talkResp = new(talkReq.RequestId, ReadOnlyMemory<byte>.Empty))
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

    private async Task HandleFindNode(Node remoteNode, Discv5FindNode findNode, CancellationToken token)
    {
        NodeRecord[] records = GetFindNodeRecords(findNode.Distances, remoteNode);
        if (records.Length == 0)
        {
            using Discv5Nodes emptyResponse = new(findNode.RequestId, 1, []);
            await SendResponse(remoteNode, emptyResponse, token);
            return;
        }

        int total = (records.Length + MaxEnrsPerNodesMessage - 1) / MaxEnrsPerNodesMessage;
        for (int i = 0; i < records.Length; i += MaxEnrsPerNodesMessage)
        {
            int count = Math.Min(MaxEnrsPerNodesMessage, records.Length - i);
            ArraySegment<NodeRecord> chunk = new(records, i, count);
            using Discv5Nodes nodes = new(findNode.RequestId, total, chunk);
            await SendResponse(remoteNode, nodes, token);
        }
    }

    private NodeRecord[] GetFindNodeRecords(Discv5Distances distances, Node requester)
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

    private Discv5Distances GetLookupDistances(Node receiver, PublicKey target)
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

        return new Discv5Distances(distances[..count]);
    }

    private Discv5RequestId CreateRequestId()
    {
        Span<byte> requestId = stackalloc byte[sizeof(ulong)];
        cryptoRandom.GenerateRandomBytes(requestId);
        int start = 0;
        while (start < requestId.Length && requestId[start] == 0)
        {
            start++;
        }

        return Discv5RequestId.From(requestId[start..]);
    }

    private bool TryGetSession(SessionKey sessionKey, [NotNullWhen(true)] out Discv5Session? session) => _sessions.TryGet(sessionKey, out session);

    private void SetSession(SessionKey sessionKey, Discv5Session session)
        => _sessions.Set(sessionKey, session);

    private bool TryGetKnownRecord(Hash256 nodeId, [NotNullWhen(true)] out NodeRecord? record) => _knownRecords.TryGet(nodeId, out record);

    private void SetKnownRecord(Hash256 nodeId, NodeRecord record)
        => _knownRecords.Set(nodeId, record);

    internal static bool IsAcceptableNodeRecord(NodeRecord record, Hash256 expectedNodeId, bool allowNonRoutable)
        => Discv5NodeRecordConverter.TryGetNodeFromEnr(record, allowNonRoutable, out Node? node) &&
            node.Id.Hash.Equals(expectedNodeId);

    internal static bool HasExpectedNodeId(NodeRecord record, Hash256 expectedNodeId)
        => record.GetObj<CompressedPublicKey>(EnrContentKey.SecP256k1)?.Decompress().Hash.Equals(expectedNodeId) == true;

    private void SetSentChallenge(ChallengeKey challengeKey, Discv5Challenge challenge, byte[] packet)
    {
        long now = Environment.TickCount64;
        TryTrimExpiredChallenges(now);
        _sentChallenges.Set(challengeKey, new SentChallenge(challenge, packet, now));
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
        foreach (KeyValuePair<ChallengeKey, SentChallenge> kv in _sentChallenges.ToArray())
        {
            if (IsExpired(kv.Value, now))
            {
                _sentChallenges.TryRemove(kv.Key, out _);
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
            await Ping(remoteNode, token);
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
