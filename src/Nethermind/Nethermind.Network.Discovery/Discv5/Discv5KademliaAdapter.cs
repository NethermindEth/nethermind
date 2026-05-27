// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Kademlia;
using Nethermind.Logging;
using Nethermind.Network;
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
    ILogManager logManager) : IDiscv5KademliaAdapter
{
    private const int MaxFindNodeRecords = 16;
    private const int MaxEnrsPerNodesMessage = 3;
    private const int MaxSessions = 4_096;
    private const int MaxSentChallenges = 4_096;
    private const int MaxPendingRequests = 4_096;
    private const int MaxResponseHandlers = 1_024;
    private const int MaxKnownRecords = 16_384;
    private const int MaxNodesResponseMessages = 16;
    private const int MaxNodesResponseRecords = 64;
    private const long SentChallengeTtlMilliseconds = 60_000;
    private static readonly TimeSpan ChallengeRateLimitWindow = TimeSpan.FromMilliseconds(100);
    private const int ChallengeRateLimitBurstPerIp = 4;
    private const int ChallengeRateLimitFilterSize = 8_192;

    private readonly TimeSpan _pingTimeout = TimeSpan.FromMilliseconds(discoveryConfig.PingTimeout);
    private readonly TimeSpan _findNodeTimeout = TimeSpan.FromMilliseconds(discoveryConfig.SendNodeTimeout);
    private readonly ILogger _logger = logManager.GetClassLogger<Discv5KademliaAdapter>();
    private readonly ConcurrentDictionary<SessionKey, Discv5Session> _sessions = new();
    private readonly ConcurrentQueue<SessionKey> _sessionKeys = new();
    private readonly ConcurrentDictionary<ChallengeKey, SentChallenge> _sentChallenges = new();
    private readonly ConcurrentQueue<ChallengeKey> _sentChallengeKeys = new();
    private long _lastSentChallengeTrimMilliseconds;
    private readonly ConcurrentDictionary<PendingNonceKey, PendingRequest> _pendingByNonce = new();
    private readonly ConcurrentQueue<PendingNonceKey> _pendingNonceKeys = new();
    private readonly ConcurrentDictionary<ResponseKey, IResponseHandler> _responseHandlers = new();
    private readonly ConcurrentQueue<ResponseKey> _responseHandlerKeys = new();
    private readonly ConcurrentDictionary<Hash256, NodeRecord> _knownRecords = new();
    private readonly ConcurrentQueue<Hash256> _knownRecordKeys = new();
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
            if (distance < 0 || distance > Hash256XorUtils.MaxDistance)
            {
                throw new ArgumentOutOfRangeException(nameof(distances), distance, $"Distance must be between 0 and {Hash256XorUtils.MaxDistance}.");
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
        byte[] requestId = CreateRequestId();
        Discv5Ping ping = new(requestId, nodeRecordProvider.Current.EnrSequence);
        PongResponseHandler responseHandler = new(receiver);

        await SendRequest(receiver, ping, Discv5MessageType.Pong, responseHandler, _pingTimeout, token);
        kademlia.Value.AddOrRefresh(receiver);
    }

    /// <inheritdoc/>
    public async Task<Node[]> FindNeighbours(Node receiver, PublicKey target, CancellationToken token)
    {
        RegisterKnownRecord(receiver);
        int[] distances = GetLookupDistances(receiver, target);
        byte[] requestId = CreateRequestId();
        Discv5FindNode findNode = new(requestId, distances);
        NodesResponseHandler responseHandler = new(receiver, distances);

        await SendRequest(receiver, findNode, Discv5MessageType.Nodes, responseHandler, _findNodeTimeout, token);
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

    private async Task SendRequest(
        Node receiver,
        Discv5Message request,
        Discv5MessageType responseType,
        IResponseHandler responseHandler,
        TimeSpan timeout,
        CancellationToken token)
    {
        ResponseKey responseKey = new(receiver.Id.Hash, RequestIdToString(request.RequestId), responseType);
        SetBounded(_responseHandlers, _responseHandlerKeys, responseKey, responseHandler, MaxResponseHandlers);

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
            byte[] packet = packetCodec.EncodeOrdinary(receiver.Id, session.WriteKey, message, session.GetNextNonce(cryptoRandom));
            await discoveryHandler.SendAsync(packet, receiver.Address);
            return null;
        }

        byte[] nonce = cryptoRandom.GenerateRandomBytes(Discv5PacketCodec.NonceSize);
        byte[] encryptionKey = cryptoRandom.GenerateRandomBytes(16);
        PendingRequest pendingRequest = new(receiver, message);
        PendingNonceKey pendingNonceKey = new(receiver.Address, NonceToString(nonce));
        SetBounded(_pendingByNonce, _pendingNonceKeys, pendingNonceKey, pendingRequest, MaxPendingRequests);

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
        PendingNonceKey pendingNonceKey = new(endpoint, NonceToString(packet.Nonce));
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

        await HandleMessage(session.RemotePublicKey, endpoint, message, token);
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

        if (nodeRecord is not null)
        {
            SetKnownRecord(nodeId, nodeRecord);
        }

        SetSession(new SessionKey(nodeId, endpoint), session);
        await HandleMessage(session.RemotePublicKey, endpoint, message, token, nodeRecord ?? knownRecord);
    }

    private async Task SendWhoAreYou(IPEndPoint endpoint, Discv5Packet requestPacket, byte[] destinationNodeId)
    {
        Hash256 nodeId = new(destinationNodeId);
        ChallengeKey challengeKey = new(nodeId, endpoint);
        long now = Environment.TickCount64;
        if (_sentChallenges.TryGetValue(challengeKey, out SentChallenge existingChallenge) && !IsExpired(existingChallenge, now))
        {
            return;
        }

        if (!TryAcceptChallenge(endpoint))
        {
            if (_logger.IsDebug) _logger.Debug($"Rate limiting discv5 WHOAREYOU challenge to {endpoint}.");
            return;
        }

        ulong enrSequence = TryGetKnownRecord(nodeId, out NodeRecord? record) ? record.EnrSequence : 0UL;
        byte[] packet = packetCodec.EncodeWhoAreYou(destinationNodeId, requestPacket.Nonce, enrSequence, out Discv5Challenge challenge);
        SetSentChallenge(challengeKey, challenge);
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
                await SendResponse(
                    remoteNode,
                    new Discv5Pong(ping.RequestId, nodeRecordProvider.Current.EnrSequence, endpoint.Address, endpoint.Port),
                    token);
                kademlia.Value.AddOrRefresh(remoteNode);
                break;
            case Discv5FindNode findNode:
                await HandleFindNode(remoteNode, findNode, token);
                kademlia.Value.AddOrRefresh(remoteNode);
                break;
            case Discv5TalkReq talkReq:
                await SendResponse(remoteNode, new Discv5TalkResp(talkReq.RequestId, []), token);
                break;
        }
    }

    private string? GetKnownEnr(Hash256 nodeId, NodeRecord? nodeRecord)
    {
        if (nodeRecord is not null)
        {
            return nodeRecord.EnrString;
        }

        return _knownRecords.TryGetValue(nodeId, out NodeRecord? knownRecord) ? knownRecord.EnrString : null;
    }

    private bool HandleResponse(Hash256 nodeId, Discv5Message message)
    {
        ResponseKey responseKey = new(nodeId, RequestIdToString(message.RequestId), message.MessageType);
        return _responseHandlers.TryGetValue(responseKey, out IResponseHandler? handler) && handler.Handle(message);
    }

    private async Task HandleFindNode(Node remoteNode, Discv5FindNode findNode, CancellationToken token)
    {
        NodeRecord[] records = GetFindNodeRecords(findNode.Distances, remoteNode);
        if (records.Length == 0)
        {
            await SendResponse(remoteNode, new Discv5Nodes(findNode.RequestId, 1, []), token);
            return;
        }

        int total = (records.Length + MaxEnrsPerNodesMessage - 1) / MaxEnrsPerNodesMessage;
        for (int i = 0; i < records.Length; i += MaxEnrsPerNodesMessage)
        {
            int count = Math.Min(MaxEnrsPerNodesMessage, records.Length - i);
            NodeRecord[] chunk = records.AsSpan(i, count).ToArray();
            await SendResponse(remoteNode, new Discv5Nodes(findNode.RequestId, total, chunk), token);
        }
    }

    private NodeRecord[] GetFindNodeRecords(int[] distances, Node requester)
    {
        HashSet<Hash256> seen = [];
        List<NodeRecord> result = [];
        bool includedSelf = false;
        for (int i = 0; i < distances.Length && result.Count < MaxFindNodeRecords; i++)
        {
            int distance = distances[i];
            if (distance < 0 || distance > Hash256XorUtils.MaxDistance)
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

            Node[] nodes = GetNodesAtDistances([distance], requester);
            for (int j = 0; j < nodes.Length && result.Count < MaxFindNodeRecords; j++)
            {
                Node node = nodes[j];
                if (string.IsNullOrEmpty(node.Enr) || !seen.Add(node.Id.Hash))
                {
                    continue;
                }

                NodeRecord? record = GetFindNodeRecord(node);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
        }

        return [.. result];
    }

    private NodeRecord? GetFindNodeRecord(Node node)
    {
        if (TryGetKnownRecord(node.Id.Hash, out NodeRecord? knownRecord))
        {
            return knownRecord;
        }

        try
        {
            return NodeRecord.FromEnrString(node.Enr);
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
            SetKnownRecord(node.Id.Hash, NodeRecord.FromEnrString(node.Enr));
        }
        catch (Exception e)
        {
            if (_logger.IsTrace) _logger.Trace($"Unable to parse known discv5 ENR for {node}: {e}");
        }
    }

    private int[] GetLookupDistances(Node receiver, PublicKey target)
    {
        KademliaHash receiverHash = KademliaHash.FromBytes(receiver.Id.Hash.Bytes);
        KademliaHash targetHash = KademliaHash.FromBytes(target.Hash.Bytes);
        int distance = Hash256XorUtils.CalculateLogDistance(receiverHash, targetHash);

        List<int> distances = [distance];
        if (distance > 0)
        {
            distances.Add(distance - 1);
        }

        if (distance < Hash256XorUtils.MaxDistance)
        {
            distances.Add(distance + 1);
        }

        return [.. distances];
    }

    private byte[] CreateRequestId()
    {
        byte[] requestId = cryptoRandom.GenerateRandomBytes(sizeof(ulong));
        return requestId.AsSpan().WithoutLeadingZeros().ToArray();
    }

    private static string RequestIdToString(byte[] requestId) => Convert.ToHexString(requestId);

    private static string NonceToString(byte[] nonce) => Convert.ToHexString(nonce);

    private bool TryGetSession(SessionKey sessionKey, [NotNullWhen(true)] out Discv5Session? session) => _sessions.TryGetValue(sessionKey, out session);

    private void SetSession(SessionKey sessionKey, Discv5Session session)
        => SetBounded(_sessions, _sessionKeys, sessionKey, session, MaxSessions);

    private bool TryGetKnownRecord(Hash256 nodeId, [NotNullWhen(true)] out NodeRecord? record) => _knownRecords.TryGetValue(nodeId, out record);

    private void SetKnownRecord(Hash256 nodeId, NodeRecord record)
        => SetBounded(_knownRecords, _knownRecordKeys, nodeId, record, MaxKnownRecords);

    private void SetSentChallenge(ChallengeKey challengeKey, Discv5Challenge challenge)
    {
        long now = Environment.TickCount64;
        TryTrimExpiredChallenges(now);
        SetBounded(_sentChallenges, _sentChallengeKeys, challengeKey, new SentChallenge(challenge, now), MaxSentChallenges);
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
        foreach (KeyValuePair<ChallengeKey, SentChallenge> kv in _sentChallenges)
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

    private static void SetBounded<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        ConcurrentQueue<TKey> insertionOrder,
        TKey key,
        TValue value,
        int maxCount)
        where TKey : notnull
    {
        if (dictionary.TryAdd(key, value))
        {
            insertionOrder.Enqueue(key);
        }
        else
        {
            dictionary[key] = value;
        }

        TrimBounded(dictionary, insertionOrder, maxCount);
    }

    private static void TrimBounded<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dictionary,
        ConcurrentQueue<TKey> insertionOrder,
        int maxCount)
        where TKey : notnull
    {
        while (dictionary.Count > maxCount && insertionOrder.TryDequeue(out TKey? key))
        {
            dictionary.TryRemove(key, out _);
        }

        if (dictionary.Count <= maxCount)
        {
            return;
        }

        foreach (KeyValuePair<TKey, TValue> kv in dictionary)
        {
            if (dictionary.Count <= maxCount)
            {
                return;
            }

            dictionary.TryRemove(kv.Key, out _);
        }
    }

    private readonly record struct SessionKey(Hash256 NodeId, IPEndPoint Endpoint);

    private readonly record struct ChallengeKey(Hash256 NodeId, IPEndPoint Endpoint);

    private readonly record struct PendingNonceKey(IPEndPoint Endpoint, string Nonce);

    private readonly record struct ResponseKey(Hash256 NodeId, string RequestId, Discv5MessageType MessageType);

    private sealed record PendingRequest(Node Receiver, Discv5Message Message);

    private readonly record struct SentChallenge(Discv5Challenge Challenge, long CreatedAtMilliseconds);

    private interface IResponseHandler
    {
        Task Task { get; }

        bool Handle(Discv5Message message);
    }

    private sealed class PongResponseHandler(Node receiver) : IResponseHandler
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Task => _completion.Task;

        public bool Handle(Discv5Message message)
        {
            if (message is not Discv5Pong pong)
            {
                return false;
            }

            receiver.ValidatedProtocol = true;
            _completion.TrySetResult();
            return true;
        }
    }

    internal sealed class NodesResponseHandler(Node receiver, int[] requestedDistances) : IResponseHandler
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Node> _nodes = [];
        private readonly HashSet<Hash256> _seenNodeIds = [];
        private readonly bool _allowNonRoutableRelays = NodeFilter.IsLoopbackOrPrivateOrLinkLocal(receiver.Address.Address);
        private int? _total;
        private int _received;

        public Task Task => _completion.Task;

        public bool Handle(Discv5Message message)
        {
            if (message is not Discv5Nodes nodes)
            {
                return false;
            }

            if (_completion.Task.IsCompleted)
            {
                return true;
            }

            if (nodes.Total <= 0 || nodes.Total > MaxNodesResponseMessages)
            {
                _completion.TrySetResult();
                return true;
            }

            if (_total is not null && _total.Value != nodes.Total)
            {
                _completion.TrySetResult();
                return true;
            }

            _total ??= nodes.Total;
            _received++;

            for (int i = 0; i < nodes.Records.Length && _nodes.Count < MaxNodesResponseRecords; i++)
            {
                NodeRecord record = nodes.Records[i];
                if (!Discv5NodeRecordConverter.TryGetNodeFromEnr(record, _allowNonRoutableRelays, out Node? node) ||
                    !_seenNodeIds.Add(node.Id.Hash) ||
                    !MatchesRequestedDistance(node, requestedDistances))
                {
                    continue;
                }

                _nodes.Add(node);
            }

            if (_received >= _total || _nodes.Count >= MaxNodesResponseRecords)
            {
                _completion.TrySetResult();
            }

            return true;
        }

        public Node[] GetNodes() => [.. _nodes];

        private bool MatchesRequestedDistance(Node node, int[] requestedDistances)
        {
            KademliaHash receiverHash = KademliaHash.FromBytes(receiver.Id.Hash.Bytes);
            KademliaHash nodeHash = KademliaHash.FromBytes(node.Id.Hash.Bytes);
            int distance = Hash256XorUtils.CalculateLogDistance(receiverHash, nodeHash);
            for (int i = 0; i < requestedDistances.Length; i++)
            {
                if (requestedDistances[i] == distance)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
