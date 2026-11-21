// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Network;
using Nethermind.Network.Enr;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.Stats.Model;

namespace Nethermind.Runner.Ethereum.WebTransport;

internal sealed class WebDiscHandler(NetworkListenerState listenerState, INodeRecordProvider records, IPeerPool peerPool)
{
    internal const int MaxRequestSize = 1;
    private const int MaxPeerRecords = 16;
    private const int MaxRecordSize = 300;
    private readonly Lock _peerCacheLock = new();
    private Node[] _cachedPeers = [];
    private long _peerCacheTimestamp;

    internal async ValueTask<byte[]> HandleAsync(ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        if (request.Span is not [Rlp.EmptyListByte])
            throw new InvalidDataException("The browser discovery request does not contain an empty RLP list.");

        bool allowLocalRecords = listenerState.WebTransport is { Address: { } address } && IPAddress.IsLoopback(address);
        NodeRecord self = await records.GetCurrentAsync(cancellationToken);
        List<NodeRecord> available = IsDialable(self, allowLocalRecords) ? [self] : [];

        foreach (Node peer in GetQuicPeers(allowLocalRecords))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (available.Count == MaxPeerRecords)
                break;
            if (GetDialableRecord(peer, allowLocalRecords) is { } record)
                available.Add(record);
        }

        return EncodePeerRecords(available);
    }

    private Node[] GetQuicPeers(bool allowLocalRecords)
    {
        lock (_peerCacheLock)
        {
            long now = Stopwatch.GetTimestamp();
            if (_peerCacheTimestamp != 0 && Stopwatch.GetElapsedTime(_peerCacheTimestamp, now) < TimeSpan.FromSeconds(5))
                return _cachedPeers;

            List<Node> peers = [];
            foreach ((_, Peer peer) in peerPool.Peers)
            {
                if (GetDialableRecord(peer.Node, allowLocalRecords) is not null)
                {
                    peers.Add(peer.Node);
                    if (peers.Count == MaxPeerRecords) break;
                }
            }

            _peerCacheTimestamp = now;
            return _cachedPeers = [.. peers];
        }
    }

    private static NodeRecord? GetDialableRecord(Node peer, bool allowLocalRecords) => peer.Enr is { Signature: not null } record &&
        record.EnrSequence >= peer.HighestObservedEnrSequence && peer.IsVerifiedEnr(record) && IsDialable(record, allowLocalRecords) ? record : null;

    private static bool IsDialable(NodeRecord record, bool allowLocalRecords)
    {
        bool hasV4 = record.TryGetQuicEndpoint(AddressFamily.InterNetwork, out IPEndPoint? v4);
        bool hasV6 = record.TryGetQuicEndpoint(AddressFamily.InterNetworkV6, out IPEndPoint? v6);
        return (hasV4 || hasV6) && (!hasV4 || IsRelayable(v4!.Address)) && (!hasV6 || IsRelayable(v6!.Address));

        bool IsRelayable(IPAddress ip)
        {
            ParsedIPAddress parsed = ParsedIPAddress.Parse(ip);
            return !parsed.IsWildcardOrNone && !parsed.IsMulticast && !parsed.IsSpecialUseAddress &&
                   (!parsed.IsLoopbackOrPrivateOrLinkLocal || allowLocalRecords && IPAddress.IsLoopback(ip));
        }
    }

    private static byte[] EncodePeerRecords(IReadOnlyList<NodeRecord> records)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(records.Count, MaxPeerRecords);
        int contentLength = 0;
        foreach (NodeRecord record in records)
        {
            int recordLength = record.GetRlpLengthWithSignature();
            if (record.Signature is null || recordLength > MaxRecordSize)
            {
                throw new ArgumentException("Browser discovery requires signed EIP-778 node records.", nameof(records));
            }

            contentLength += recordLength;
        }

        byte[] response = new byte[Rlp.LengthOfSequence(contentLength)];
        RlpWriter writer = RlpWriter.ContentWriter(response);
        RlpSequence list = writer.StartSequence(contentLength);
        foreach (NodeRecord record in records)
        {
            record.Encode(ref writer);
        }

        writer.EndSequence(list);
        return response;
    }
}
