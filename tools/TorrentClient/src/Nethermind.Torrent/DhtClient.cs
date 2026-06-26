// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;

namespace Nethermind.Torrent;

internal sealed class DhtClient : IAsyncDisposable
{
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
        BDictionary args = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("id", Bencode.Bytes(_nodeId.Bytes)),
            new KeyValuePair<string, BValue>("info_hash", Bencode.Bytes(infoHash)));

        BDictionary? response = await QueryAsync(node.EndPoint, "get_peers", args, token, node.Id);
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

    private async Task<BDictionary?> QueryAsync(
        IPEndPoint endpoint,
        string queryName,
        BDictionary arguments,
        CancellationToken token,
        KadId? expectedNodeId = null)
    {
        byte[] transactionBytes = NextTransactionId();
        BDictionary query = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("t", Bencode.Bytes(transactionBytes)),
            new KeyValuePair<string, BValue>("y", Bencode.String("q")),
            new KeyValuePair<string, BValue>("q", Bencode.String(queryName)),
            new KeyValuePair<string, BValue>("a", arguments));
        byte[] payload = Bencode.Encode(query);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await _udpClient.SendAsync(payload, endpoint, timeout.Token);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult result = await _udpClient.ReceiveAsync(timeout.Token);
                if (!result.RemoteEndPoint.Equals(endpoint))
                {
                    continue;
                }

                BDictionary root = BencodeDocument.Decode(result.Buffer).Root.AsDictionary("dht response");
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
        return ValueTask.CompletedTask;
    }
}
