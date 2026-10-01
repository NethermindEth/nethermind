// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Nethermind.Torrent;

internal sealed class DhtClient : IAsyncDisposable
{
    private const int MaxAnnounceQueries = 32;
    private const int MaxAnnounceNodes = 8;
    private const int MaxItemQueries = 32;
    private const int MaxItemResponseBytes = 4096;

    private static readonly (string Host, int Port)[] BootstrapRouters =
    [
        ("router.bittorrent.com", 6881),
        ("dht.transmissionbt.com", 6881),
        ("router.utorrent.com", 6881),
    ];

    private readonly KadId _nodeId = KadId.Random();
    private readonly UdpClient _udpClient = new(0);
    private readonly TorrentKademlia _kademlia;
    private readonly Action<string> _log;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private int _transactionId;

    public DhtClient(byte[] peerId, Action<string> log)
    {
        if (peerId.Length != KadId.Length)
        {
            throw new ArgumentException("Peer id must be 20 bytes.", nameof(peerId));
        }

        _kademlia = new TorrentKademlia(_nodeId, alpha: 1);
        _log = log;
    }

    public async Task<IReadOnlyList<PeerEndpoint>> FindPeersAsync(byte[] infoHash, CancellationToken token)
    {
        if (infoHash.Length != KadId.Length)
        {
            throw new ArgumentException("Info hash must be 20 bytes.", nameof(infoHash));
        }

        await _operationGate.WaitAsync(token);
        try
        {
            return await FindPeersCoreAsync(infoHash, token);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>Announces the listening peer to nearby DHT nodes and returns the number of acknowledged announcements.</summary>
    /// <param name="infoHash">The 20-byte torrent info hash.</param>
    /// <param name="listenPort">The active inbound peer-wire TCP port.</param>
    /// <param name="isSeed">Whether the local peer has the complete torrent.</param>
    /// <param name="token">Cancels discovery and outstanding queries.</param>
    public async Task<int> AnnounceAsync(byte[] infoHash, int listenPort, bool isSeed, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(infoHash);
        if (infoHash.Length != KadId.Length)
        {
            throw new ArgumentException("Info hash must be 20 bytes.", nameof(infoHash));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(listenPort, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(listenPort, ushort.MaxValue);

        await _operationGate.WaitAsync(token);
        try
        {
            return await AnnounceCoreAsync(infoHash, listenPort, isSeed, token);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task<byte[]?> GetImmutableAsync(byte[] target, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length != KadId.Length)
        {
            throw new ArgumentException("DHT target must be 20 bytes.", nameof(target));
        }

        byte[] stableTarget = target.ToArray();
        await _operationGate.WaitAsync(token);
        try
        {
            (byte[]? value, _, _) = await GetItemCoreAsync(stableTarget, null, [], token);
            return value;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task<DhtMutableItem?> GetMutableAsync(byte[] publicKey, byte[] salt, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        ArgumentNullException.ThrowIfNull(salt);
        if (publicKey.Length != 32 || salt.Length > 64)
        {
            throw new ArgumentException("BEP 44 requires a 32-byte key and at most 64 bytes of salt.");
        }

        byte[] stableKey = publicKey.ToArray();
        byte[] stableSalt = salt.ToArray();
        byte[] target = SHA1.HashData([.. stableKey, .. stableSalt]);
        await _operationGate.WaitAsync(token);
        try
        {
            (_, DhtMutableItem? item, _) = await GetItemCoreAsync(target, stableKey, stableSalt, token);
            return item;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task<int> PutImmutableAsync(byte[] value, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] stableValue = value.ToArray();
        DhtMutableItem.Validate(stableValue, 0, []);
        return await PutItemAsync(SHA1.HashData(stableValue), stableValue, null, null, token);
    }

    internal Task<int> PutMutableAsync(DhtMutableItem item, long? compareAndSwap, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (compareAndSwap < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(compareAndSwap));
        }

        // Recheck because the public byte arrays may have been mutated by a caller.
        DhtMutableItem validated = DhtMutableItem.FromSigned(item.PublicKey, item.Salt, item.Sequence, item.Signature, item.Value);
        return PutItemAsync(validated.Target, validated.Value, validated, compareAndSwap, token);
    }

    private async Task<int> PutItemAsync(byte[] target, byte[] value, DhtMutableItem? item, long? compareAndSwap, CancellationToken token)
    {
        await _operationGate.WaitAsync(token);
        try
        {
            (_, _, List<(DhtNode Node, byte[] Token)> eligible) = await GetItemCoreAsync(target, item?.PublicKey, item?.Salt ?? [], token);
            eligible.Sort((left, right) => DhtKeyOperator.CompareDistance(left.Node.Id, right.Node.Id, new KadId(target)));
            int stored = 0;
            for (int i = 0; i < eligible.Count && i < MaxAnnounceNodes; i++)
            {
                token.ThrowIfCancellationRequested();
                (DhtNode node, byte[] writeToken) = eligible[i];
                BDictionary args = Bencode.Dictionary(
                    new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
                    new KeyValuePair<string, BValue>("token", Bencode.Bytes(writeToken)),
                    new KeyValuePair<string, BValue>("v", new BRaw(value)));
                if (item is not null)
                {
                    args.Values.Add("k", Bencode.Bytes(item.PublicKey));
                    args.Values.Add("seq", Bencode.Integer(item.Sequence));
                    args.Values.Add("sig", Bencode.Bytes(item.Signature));
                    if (item.Salt.Length != 0)
                    {
                        args.Values.Add("salt", Bencode.Bytes(item.Salt));
                    }

                    if (compareAndSwap.HasValue)
                    {
                        args.Values.Add("cas", Bencode.Integer(compareAndSwap.Value));
                    }
                }

                if (await QueryAsync(node.EndPoint, "put", args, token, node.Id, requireCanonical: true) is not null)
                {
                    stored++;
                }
            }

            return stored;
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private async Task<(byte[]? Immutable, DhtMutableItem? Mutable, List<(DhtNode Node, byte[] Token)> Eligible)> GetItemCoreAsync(
        byte[] target, byte[]? publicKey, byte[] salt, CancellationToken token)
    {
        await BootstrapAsync(token);
        KadId targetId = new(target);
        List<DhtNode> candidates = _kademlia.GetClosest(targetId, 16);
        HashSet<KadId> queried = [];
        List<(DhtNode Node, byte[] Token)> eligible = [];
        byte[]? immutable = null;
        DhtMutableItem? mutable = null;
        using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(50));
        try
        {
            for (int queryCount = 0; queryCount < MaxItemQueries; queryCount++)
            {
                DhtNode? next = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!queried.Contains(candidates[i].Id))
                    {
                        next = candidates[i];
                        break;
                    }
                }

                if (next is null)
                {
                    break;
                }

                DhtNode node = next.Value;
                queried.Add(node.Id);
                BDictionary args = Bencode.Dictionary(
                    new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
                    new KeyValuePair<string, BValue>("target", Bencode.Bytes(target)));
                if (mutable is not null)
                {
                    args.Values.Add("seq", Bencode.Integer(mutable.Sequence));
                }

                BDictionary? response = await QueryAsync(node.EndPoint, "get", args, budget.Token, node.Id,
                    requireCanonical: true, responseTimeout: TimeSpan.FromMilliseconds(1500));
                if (response is null)
                {
                    continue;
                }

                if (response.TryGetValue("token", out BValue? rawToken) && rawToken is BString writeToken &&
                    writeToken.Bytes.Length is > 0 and <= 256)
                {
                    eligible.Add((node, writeToken.Bytes));
                }

                if (response.RawItemValue is byte[] value)
                {
                    if (value.Length <= 1000)
                    {
                        if (publicKey is null && SHA1.HashData(value).AsSpan().SequenceEqual(target))
                        {
                            immutable = value;
                        }
                        else if (publicKey is not null &&
                            response.TryGetValue("k", out BValue? rawKey) && rawKey is BString key &&
                            key.Bytes.AsSpan().SequenceEqual(publicKey) &&
                            response.TryGetValue("seq", out BValue? rawSequence) && rawSequence is BInteger sequence &&
                            response.TryGetValue("sig", out BValue? rawSignature) && rawSignature is BString signature)
                        {
                            try
                            {
                                DhtMutableItem candidate = DhtMutableItem.FromSigned(key.Bytes, salt, sequence.Value, signature.Bytes, value);
                                if (mutable is not null && candidate.Sequence == mutable.Sequence &&
                                    !candidate.Value.AsSpan().SequenceEqual(mutable.Value))
                                {
                                    throw new InvalidDataException("Conflicting signed BEP 44 values have the same sequence.");
                                }

                                if (candidate.Target.AsSpan().SequenceEqual(target) &&
                                    (mutable is null || candidate.Sequence > mutable.Sequence))
                                {
                                    mutable = candidate;
                                }
                            }
                            catch (FormatException)
                            {
                                _log($"dht get {node.EndPoint} returned an invalid mutable item");
                            }
                        }
                    }
                }

                if (response.TryGetValue("nodes", out BValue? rawNodes) && rawNodes is BString nodes)
                {
                    List<DhtNode> discovered = [];
                    ParseCompactNodes(nodes.Bytes.AsSpan(0, Math.Min(nodes.Bytes.Length, MaxItemQueries * 26)), discovered);
                    for (int i = 0; i < discovered.Count; i++)
                    {
                        AddSeed(candidates, discovered[i], targetId);
                        if (candidates.Count > MaxItemQueries)
                        {
                            candidates.RemoveAt(candidates.Count - 1);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            _log("dht item lookup timed out");
        }

        token.ThrowIfCancellationRequested();
        return (immutable, mutable, eligible);
    }

    private async Task<IReadOnlyList<PeerEndpoint>> FindPeersCoreAsync(byte[] infoHash, CancellationToken token)
    {
        List<PeerEndpoint> peers = [];
        await BootstrapAsync(token);

        KadId target = new(infoHash);
        List<DhtNode> seed = _kademlia.GetClosest(target, 16);
        if (seed.Count == 0)
        {
            await BootstrapForTargetAsync(infoHash, peers, seed, token);
        }

        if (seed.Count == 0)
        {
            return peers;
        }

        for (int i = 0; i < seed.Count && peers.Count == 0; i++)
        {
            await QueryGetPeersAsync(seed[i], infoHash, peers, token);
        }

        if (peers.Count == 0)
        {
            await _kademlia.LookupAsync(
                target,
                async (node, queryToken) =>
                {
                    List<DhtNode> nodes = [];
                    if (!await QueryGetPeersAsync(node, infoHash, peers, queryToken, nodes))
                    {
                        throw new TimeoutException($"DHT get_peers query to {node.EndPoint} did not receive a valid response.");
                    }

                    return nodes;
                },
                token);
        }

        if (peers.Count != 0)
        {
            _log($"dht peers: {peers.Count}");
        }

        return peers;
    }

    private async Task<int> AnnounceCoreAsync(byte[] infoHash, int listenPort, bool isSeed, CancellationToken token)
    {
        KadId target = new(infoHash);
        List<(DhtNode Node, byte[] Token)> eligible = [];
        HashSet<KadId> queried = [];
        using (CancellationTokenSource discoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            discoveryBudget.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                await BootstrapAsync(discoveryBudget.Token);
                List<DhtNode> candidates = _kademlia.GetClosest(target, 16);
                for (int queryCount = 0; queryCount < MaxAnnounceQueries; queryCount++)
                {
                    DhtNode? next = null;
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (!queried.Contains(candidates[i].Id))
                        {
                            next = candidates[i];
                            break;
                        }
                    }

                    if (next is null)
                    {
                        break;
                    }

                    DhtNode node = next.Value;
                    queried.Add(node.Id);
                    BDictionary? response = await QueryGetPeersResponseAsync(node, infoHash, discoveryBudget.Token);
                    if (response is null)
                    {
                        continue;
                    }

                    if (response.TryGetValue("token", out BValue? tokenValue) && tokenValue is BString writeToken &&
                        writeToken.Bytes.Length is > 0 and <= 256)
                    {
                        eligible.Add((node, writeToken.Bytes));
                    }

                    if (response.TryGetValue("nodes", out BValue? nodesValue) && nodesValue is BString compactNodes)
                    {
                        List<DhtNode> nodes = [];
                        ParseCompactNodes(compactNodes.Bytes, nodes);
                        for (int i = 0; i < nodes.Count; i++)
                        {
                            AddSeed(candidates, nodes[i], target);
                            if (candidates.Count > MaxAnnounceQueries)
                            {
                                candidates.RemoveAt(candidates.Count - 1);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                _log("dht announce discovery timed out");
            }
        }

        eligible.Sort((left, right) => DhtKeyOperator.CompareDistance(left.Node.Id, right.Node.Id, target));
        int announced = 0;
        using CancellationTokenSource announceBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
        announceBudget.CancelAfter(TimeSpan.FromSeconds(20));
        for (int i = 0; i < eligible.Count && i < MaxAnnounceNodes; i++)
        {
            token.ThrowIfCancellationRequested();
            (DhtNode node, byte[] writeToken) = eligible[i];
            BDictionary args = Bencode.Dictionary(
                new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
                new KeyValuePair<string, BValue>("info_hash", Bencode.Bytes(infoHash)),
                new KeyValuePair<string, BValue>("port", Bencode.Integer(listenPort)),
                new KeyValuePair<string, BValue>("token", Bencode.Bytes(writeToken)));
            if (isSeed)
            {
                args.Values.Add("seed", Bencode.Integer(1));
            }

            try
            {
                if (await QueryAsync(node.EndPoint, "announce_peer", args, announceBudget.Token, node.Id) is not null)
                {
                    announced++;
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                break;
            }
        }

        token.ThrowIfCancellationRequested();
        return announced;
    }

    private async Task BootstrapAsync(CancellationToken token)
    {
        if (_kademlia.GetClosest(_nodeId, 1).Count != 0)
        {
            return;
        }

        for (int i = 0; i < BootstrapRouters.Length; i++)
        {
            (string host, int port) = BootstrapRouters[i];
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token);
                for (int j = 0; j < addresses.Length; j++)
                {
                    if (addresses[j].AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    IPEndPoint endpoint = new(addresses[j], port);
                    List<DhtNode> nodes = await QueryFindNodeAsync(endpoint, _nodeId.Bytes.ToArray(), token);
                    if (nodes.Count != 0)
                    {
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log($"dht bootstrap {host}:{port} failed: {exception.Message}");
            }
        }
    }

    private async Task BootstrapForTargetAsync(byte[] infoHash, List<PeerEndpoint> peers, List<DhtNode> seed, CancellationToken token)
    {
        KadId target = new(infoHash);
        for (int i = 0; i < BootstrapRouters.Length && seed.Count == 0; i++)
        {
            (string host, int port) = BootstrapRouters[i];
            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, token);
                for (int j = 0; j < addresses.Length && seed.Count == 0; j++)
                {
                    if (addresses[j].AddressFamily != AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    List<DhtNode> nodes = await QueryFindNodeAsync(new IPEndPoint(addresses[j], port), infoHash, token);
                    for (int k = 0; k < nodes.Count && seed.Count < 16 && peers.Count == 0; k++)
                    {
                        List<DhtNode> moreNodes = [];
                        await QueryGetPeersAsync(nodes[k], infoHash, peers, token, moreNodes);
                        if (peers.Count != 0)
                        {
                            break;
                        }

                        AddSeed(seed, nodes[k], target);
                        for (int m = 0; m < moreNodes.Count && seed.Count < 16; m++)
                        {
                            AddSeed(seed, moreNodes[m], target);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _log($"dht bootstrap {host}:{port} failed: {exception.Message}");
            }
        }
    }

    private async Task<List<DhtNode>> QueryFindNodeAsync(IPEndPoint endpoint, byte[] target, CancellationToken token)
    {
        BDictionary args = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
            new KeyValuePair<string, BValue>("target", Bencode.Bytes(target)));

        BDictionary? response = await QueryAsync(endpoint, "find_node", args, token);
        List<DhtNode> nodes = [];
        if (response is not null && response.TryGetValue("nodes", out BValue? rawNodes) && rawNodes is BString compactNodes)
        {
            ParseCompactNodes(compactNodes.Bytes, nodes);
        }

        return nodes;
    }

    private async Task<bool> QueryGetPeersAsync(DhtNode node, byte[] infoHash, List<PeerEndpoint> peers, CancellationToken token)
        => await QueryGetPeersAsync(node, infoHash, peers, token, null);

    private async Task<bool> QueryGetPeersAsync(
        DhtNode node,
        byte[] infoHash,
        List<PeerEndpoint> peers,
        CancellationToken token,
        List<DhtNode>? nodes)
    {
        BDictionary? response = await QueryGetPeersResponseAsync(node, infoHash, token);
        if (response is null)
        {
            return false;
        }

        bool hasValidPayload = false;
        if (response.TryGetValue("values", out BValue? valuesValue) && valuesValue is BList values)
        {
            for (int i = 0; i < values.Values.Count; i++)
            {
                if (values.Values[i] is BString compactPeer)
                {
                    hasValidPayload |= ParseCompactPeers(compactPeer.Bytes, peers) != 0;
                }
            }
        }

        if (response.TryGetValue("nodes", out BValue? nodesValue) && nodesValue is BString compactNodes)
        {
            List<DhtNode> parsedNodes = [];
            hasValidPayload |= ParseCompactNodes(compactNodes.Bytes, parsedNodes) != 0;
            for (int i = 0; i < parsedNodes.Count; i++)
            {
                nodes?.Add(parsedNodes[i]);
            }
        }

        return hasValidPayload;
    }

    private Task<BDictionary?> QueryGetPeersResponseAsync(DhtNode node, byte[] infoHash, CancellationToken token)
    {
        BDictionary args = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
            new KeyValuePair<string, BValue>("info_hash", Bencode.Bytes(infoHash)));
        return QueryAsync(node.EndPoint, "get_peers", args, token, node.Id);
    }

    private async Task<BDictionary?> QueryAsync(
        IPEndPoint endpoint,
        string queryName,
        BDictionary arguments,
        CancellationToken token,
        KadId? expectedNodeId = null,
        bool requireCanonical = false,
        TimeSpan? responseTimeout = null)
    {
        byte[] transactionBytes = NextTransactionId();
        BDictionary query = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("t", Bencode.Bytes(transactionBytes)),
            new KeyValuePair<string, BValue>("y", Bencode.String("q")),
            new KeyValuePair<string, BValue>("q", Bencode.String(queryName)),
            new KeyValuePair<string, BValue>("a", arguments));
        byte[] payload = Bencode.Encode(query);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(responseTimeout ?? TimeSpan.FromSeconds(5));
        bool validResponse = false;
        try
        {
            await _udpClient.SendAsync(payload, endpoint, timeout.Token);
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync(timeout.Token);
                if (!result.RemoteEndPoint.Equals(endpoint))
                {
                    continue;
                }

                if (requireCanonical && result.Buffer.Length > MaxItemResponseBytes)
                {
                    continue;
                }

                BDictionary root = BencodeDocument.Decode(result.Buffer, requireCanonical).Root.AsDictionary("dht response");
                if (!root.TryGetValue("t", out BValue? transaction) ||
                    transaction is not BString transactionString ||
                    !transactionString.Bytes.AsSpan().SequenceEqual(transactionBytes))
                {
                    continue;
                }

                if (!root.TryGetValue("y", out BValue? yValue) || yValue is null || yValue.AsText("y") != "r")
                {
                    return null;
                }

                BDictionary response = root["r"].AsDictionary("r");
                KadId? responseNodeId = null;
                if (response.TryGetValue("id", out BValue? remoteId) && remoteId is BString remoteIdString && remoteIdString.Bytes.Length == KadId.Length)
                {
                    responseNodeId = new KadId(remoteIdString.Bytes);
                }

                if (!responseNodeId.HasValue)
                {
                    return null;
                }

                if (expectedNodeId.HasValue && !responseNodeId.Value.Equals(expectedNodeId.Value))
                {
                    return null;
                }

                _kademlia.AddOrRefresh(new DhtNode(responseNodeId.Value, result.RemoteEndPoint));
                validResponse = true;

                return response;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _log($"dht {queryName} {endpoint} failed: {exception.Message}");
            return null;
        }
        finally
        {
            if (!validResponse && expectedNodeId.HasValue && !token.IsCancellationRequested)
            {
                _kademlia.Remove(new DhtNode(expectedNodeId.Value, endpoint));
            }
        }

        return null;
    }

    private byte[] NextTransactionId()
    {
        int id = Interlocked.Increment(ref _transactionId);
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, id);
        return bytes;
    }

    private static void AddSeed(List<DhtNode> seed, DhtNode node, KadId target)
    {
        if (Contains(seed, node))
        {
            return;
        }

        seed.Add(node);
        seed.Sort((left, right) =>
            DhtKeyOperator.CompareDistance(left.Id, right.Id, target));
    }

    private static int ParseCompactNodes(ReadOnlySpan<byte> bytes, List<DhtNode> nodes)
    {
        int parsedCount = 0;
        for (int i = 0; i + 26 <= bytes.Length; i += 26)
        {
            KadId id = new(bytes.Slice(i, KadId.Length));
            IPAddress address = new(bytes.Slice(i + KadId.Length, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + KadId.Length + 4, 2));
            if (port != 0)
            {
                nodes.Add(new DhtNode(id, new IPEndPoint(address, port)));
                parsedCount++;
            }
        }

        return parsedCount;
    }

    private static int ParseCompactPeers(ReadOnlySpan<byte> bytes, List<PeerEndpoint> peers)
    {
        int parsedCount = 0;
        for (int i = 0; i + 6 <= bytes.Length; i += 6)
        {
            IPAddress address = new(bytes.Slice(i, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 4, 2));
            if (port != 0)
            {
                parsedCount++;
                PeerEndpoint peer = new(address.ToString(), port);
                if (!Contains(peers, peer))
                {
                    peers.Add(peer);
                }
            }
        }

        return parsedCount;
    }

    private static bool Contains(List<PeerEndpoint> peers, PeerEndpoint peer)
    {
        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].Equals(peer))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(List<DhtNode> nodes, DhtNode node)
    {
        for (int i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].Equals(node))
            {
                return true;
            }
        }

        return false;
    }

    public ValueTask DisposeAsync()
    {
        _udpClient.Dispose();
        _operationGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
