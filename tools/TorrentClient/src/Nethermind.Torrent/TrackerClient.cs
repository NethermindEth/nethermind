// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Nethermind.Torrent;

internal sealed class TrackerClient(HttpClient httpClient, Action<string> log, TimeSpan trackerTimeout)
{
    private const int MaxTrackerResponseBytes = 1024 * 1024;
    private const int NumWant = 120;
    private const long UdpConnectMagic = 0x41727101980;

    private readonly HttpClient _httpClient = httpClient;
    private readonly Action<string> _log = log;
    private readonly TimeSpan _trackerTimeout = trackerTimeout;
    private readonly HashSet<Uri> _startedTrackers = [];

    public async Task<TrackerAnnounceResult> AnnounceAsync(
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        CancellationToken token)
    {
        List<PeerEndpoint> peers = [];
        TimeSpan? interval = null;
        for (int i = 0; i < torrent.Trackers.Count; i++)
        {
            Uri tracker = torrent.Trackers[i];
            string? announceEvent = _startedTrackers.Contains(tracker) ? null : "started";
            try
            {
                TrackerAnnounceResult trackerResult = await AnnounceOneAsync(
                    tracker,
                    torrent,
                    peerId,
                    trackerKey,
                    listenPort,
                    downloaded,
                    uploaded,
                    announceEvent,
                    token);
                _startedTrackers.Add(tracker);
                AddDistinct(peers, trackerResult.Peers);
                if (peers.Count > 0)
                {
                    return new TrackerAnnounceResult(peers, trackerResult.Interval);
                }

                interval ??= trackerResult.Interval;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                _log($"tracker {tracker} failed: {exception.Message}");
            }
            catch (Exception exception)
            {
                _log($"tracker {tracker} failed: {exception.Message}");
            }
        }

        return new TrackerAnnounceResult(peers, interval ?? TimeSpan.FromMinutes(1));
    }

    public async Task AnnounceEventAsync(
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        string announceEvent,
        CancellationToken token)
    {
        Uri[] trackers = [.. _startedTrackers];
        for (int i = 0; i < trackers.Length; i++)
        {
            try
            {
                await AnnounceOneAsync(
                    trackers[i],
                    torrent,
                    peerId,
                    trackerKey,
                    listenPort,
                    downloaded,
                    uploaded,
                    announceEvent,
                    token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException exception)
            {
                _log($"tracker {trackers[i]} {announceEvent} failed: {exception.Message}");
            }
            catch (Exception exception)
            {
                _log($"tracker {trackers[i]} {announceEvent} failed: {exception.Message}");
            }
        }
    }

    private async Task<TrackerAnnounceResult> AnnounceOneAsync(
        Uri tracker,
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        string? announceEvent,
        CancellationToken token)
    {
        if (tracker.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            tracker.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return await AnnounceHttpAsync(tracker, torrent, peerId, trackerKey, listenPort, downloaded, uploaded, announceEvent, token);
        }

        if (tracker.Scheme.Equals("udp", StringComparison.OrdinalIgnoreCase))
        {
            return await AnnounceUdpAsync(tracker, torrent, peerId, trackerKey, listenPort, downloaded, uploaded, announceEvent, token);
        }

        _log($"tracker {tracker} skipped: unsupported scheme");
        return new TrackerAnnounceResult([], TimeSpan.FromMinutes(5));
    }

    private async Task<TrackerAnnounceResult> AnnounceHttpAsync(
        Uri tracker,
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        string? announceEvent,
        CancellationToken token)
    {
        Uri announceUri = BuildAnnounceUri(tracker, torrent, peerId, trackerKey, listenPort, downloaded, uploaded, announceEvent);
        using HttpResponseMessage response = await _httpClient.GetAsync(announceUri, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        byte[] payload = await ReadLimitedContentAsync(response, token);
        BDictionary root = BencodeDocument.Decode(payload).Root.AsDictionary("tracker response");

        if (root.TryGetValue("failure reason", out BValue? failure) && failure is not null)
        {
            throw new InvalidOperationException(failure.AsText("failure reason"));
        }

        List<PeerEndpoint> peers = [];
        if (root.TryGetValue("peers", out BValue? peersValue) && peersValue is not null)
        {
            ParsePeers(peersValue, peers);
        }

        if (root.TryGetValue("peers6", out BValue? peers6Value) && peers6Value is BString peers6)
        {
            ParseCompactPeers6(peers6.Bytes, peers);
        }

        TimeSpan interval = TimeSpan.FromSeconds(GetInteger(root, "interval", 300));
        return new TrackerAnnounceResult(peers, interval);
    }

    private static async Task<byte[]> ReadLimitedContentAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxTrackerResponseBytes)
        {
            throw new InvalidDataException($"Tracker response exceeds {MaxTrackerResponseBytes} bytes.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(token);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        while (true)
        {
            int read = await stream.ReadAsync(chunk, token);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > MaxTrackerResponseBytes)
            {
                throw new InvalidDataException($"Tracker response exceeds {MaxTrackerResponseBytes} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private async Task<TrackerAnnounceResult> AnnounceUdpAsync(
        Uri tracker,
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        string? announceEvent,
        CancellationToken token)
    {
        IPEndPoint endpoint = await ResolveUdpTrackerAsync(tracker, token);
        using UdpClient udpClient = new(endpoint.AddressFamily);
        udpClient.Connect(endpoint);
        int transactionId = Random.Shared.Next();
        byte[] connectRequest = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(connectRequest.AsSpan(0, 8), UdpConnectMagic);
        BinaryPrimitives.WriteInt32BigEndian(connectRequest.AsSpan(8, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(connectRequest.AsSpan(12, 4), transactionId);
        byte[] connectResponse = await SendReceiveUdpAsync(udpClient, connectRequest, transactionId, _trackerTimeout, token);
        if (connectResponse.Length < 16 || BinaryPrimitives.ReadInt32BigEndian(connectResponse.AsSpan(0, 4)) != 0)
        {
            throw new InvalidDataException("Invalid UDP tracker connect response.");
        }

        long connectionId = BinaryPrimitives.ReadInt64BigEndian(connectResponse.AsSpan(8, 8));
        transactionId = Random.Shared.Next();
        byte[] announceRequest = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(0, 8), connectionId);
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(12, 4), transactionId);
        torrent.InfoHash.CopyTo(announceRequest.AsSpan(16, TorrentMetadata.Sha1Length));
        peerId.CopyTo(announceRequest.AsSpan(36, TorrentMetadata.Sha1Length));
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(56, 8), downloaded);
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(64, 8), Math.Max(0, torrent.TotalLength - downloaded));
        BinaryPrimitives.WriteInt64BigEndian(announceRequest.AsSpan(72, 8), uploaded);
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(80, 4), GetUdpEventId(announceEvent));
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(84, 4), 0);
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(88, 4), unchecked((int)Convert.ToUInt32(trackerKey, 16)));
        BinaryPrimitives.WriteInt32BigEndian(announceRequest.AsSpan(92, 4), NumWant);
        BinaryPrimitives.WriteUInt16BigEndian(announceRequest.AsSpan(96, 2), checked((ushort)listenPort));

        byte[] announceResponse = await SendReceiveUdpAsync(udpClient, announceRequest, transactionId, _trackerTimeout, token);
        int action = BinaryPrimitives.ReadInt32BigEndian(announceResponse.AsSpan(0, 4));
        if (action == 3)
        {
            throw new InvalidOperationException(Encoding.UTF8.GetString(announceResponse.AsSpan(8)));
        }

        if (announceResponse.Length < 20 || action != 1)
        {
            throw new InvalidDataException("Invalid UDP tracker announce response.");
        }

        int intervalSeconds = BinaryPrimitives.ReadInt32BigEndian(announceResponse.AsSpan(8, 4));
        List<PeerEndpoint> peers = [];
        if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
        {
            ParseCompactPeers6(announceResponse.AsSpan(20), peers);
        }
        else
        {
            ParseCompactPeers(announceResponse.AsSpan(20), peers);
        }

        return new TrackerAnnounceResult(peers, TimeSpan.FromSeconds(Math.Max(30, intervalSeconds)));
    }

    private static async Task<IPEndPoint> ResolveUdpTrackerAsync(Uri tracker, CancellationToken token)
    {
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(tracker.Host, token);
        for (int i = 0; i < addresses.Length; i++)
        {
            if (addresses[i].AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
            {
                return new IPEndPoint(addresses[i], tracker.Port);
            }
        }

        throw new InvalidOperationException($"No IPv4 or IPv6 address found for UDP tracker {tracker.Host}.");
    }

    private static async Task<byte[]> SendReceiveUdpAsync(UdpClient udpClient, byte[] request, int transactionId, TimeSpan trackerTimeout, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(trackerTimeout);
        await udpClient.SendAsync(request, timeout.Token);
        while (true)
        {
            UdpReceiveResult result = await udpClient.ReceiveAsync(timeout.Token);
            if (result.Buffer.Length < 8)
            {
                continue;
            }

            int responseTransactionId = BinaryPrimitives.ReadInt32BigEndian(result.Buffer.AsSpan(4, 4));
            if (responseTransactionId == transactionId)
            {
                return result.Buffer;
            }
        }
    }

    private static int GetUdpEventId(string? announceEvent)
        => announceEvent switch
        {
            null => 0,
            "completed" => 1,
            "started" => 2,
            "stopped" => 3,
            _ => 0,
        };

    private static long GetInteger(BDictionary dictionary, string key, long defaultValue)
        => dictionary.TryGetValue(key, out BValue? value) && value is not null
            ? value.AsInteger(key)
            : defaultValue;

    private static Uri BuildAnnounceUri(
        Uri tracker,
        TorrentMetadata torrent,
        byte[] peerId,
        string trackerKey,
        int listenPort,
        long downloaded,
        long uploaded,
        string? announceEvent)
    {
        StringBuilder query = new();
        AppendRawParameter(query, "info_hash", torrent.InfoHash);
        AppendRawParameter(query, "peer_id", peerId);
        AppendParameter(query, "port", listenPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendParameter(query, "uploaded", uploaded.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendParameter(query, "downloaded", downloaded.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendParameter(query, "left", Math.Max(0, torrent.TotalLength - downloaded).ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendParameter(query, "compact", "1");
        AppendParameter(query, "numwant", NumWant.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendParameter(query, "key", trackerKey);
        if (announceEvent is not null)
        {
            AppendParameter(query, "event", announceEvent);
        }

        UriBuilder builder = new(tracker);
        if (builder.Query.Length > 1)
        {
            builder.Query = builder.Query[1..] + "&" + query;
        }
        else
        {
            builder.Query = query.ToString();
        }

        return builder.Uri;
    }

    private static void AppendRawParameter(StringBuilder query, string name, ReadOnlySpan<byte> value)
    {
        AppendSeparator(query);
        query.Append(name);
        query.Append('=');
        AppendEscapedBytes(query, value);
    }

    private static void AppendParameter(StringBuilder query, string name, string value)
    {
        AppendSeparator(query);
        query.Append(Uri.EscapeDataString(name));
        query.Append('=');
        query.Append(Uri.EscapeDataString(value));
    }

    private static void AppendSeparator(StringBuilder query)
    {
        if (query.Length != 0)
        {
            query.Append('&');
        }
    }

    private static void AppendEscapedBytes(StringBuilder builder, ReadOnlySpan<byte> bytes)
    {
        const string hex = "0123456789ABCDEF";
        for (int i = 0; i < bytes.Length; i++)
        {
            byte value = bytes[i];
            if (IsUnreserved(value))
            {
                builder.Append((char)value);
            }
            else
            {
                builder.Append('%');
                builder.Append(hex[value >> 4]);
                builder.Append(hex[value & 0x0f]);
            }
        }
    }

    private static bool IsUnreserved(byte value)
        => value is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-'
            or (byte)'_'
            or (byte)'.'
            or (byte)'~';

    internal static void ParsePeers(BValue peersValue, List<PeerEndpoint> peers)
    {
        if (peersValue is BString compact)
        {
            ParseCompactPeers(compact.Bytes, peers);
            return;
        }

        BList list = peersValue.AsList("peers");
        for (int i = 0; i < list.Values.Count; i++)
        {
            BDictionary item = list.Values[i].AsDictionary("peer");
            string host = item["ip"].AsText("peer.ip");
            long port = item["port"].AsInteger("peer.port");
            if (port > 0 && port <= ushort.MaxValue)
            {
                peers.Add(new PeerEndpoint(host, (int)port));
            }
        }
    }

    private static void ParseCompactPeers(ReadOnlySpan<byte> bytes, List<PeerEndpoint> peers)
    {
        for (int i = 0; i + 6 <= bytes.Length; i += 6)
        {
            IPAddress address = new(bytes.Slice(i, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 4, 2));
            if (port != 0)
            {
                peers.Add(new PeerEndpoint(address.ToString(), port));
            }
        }
    }

    private static void ParseCompactPeers6(ReadOnlySpan<byte> bytes, List<PeerEndpoint> peers)
    {
        for (int i = 0; i + 18 <= bytes.Length; i += 18)
        {
            IPAddress address = new(bytes.Slice(i, 16));
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(i + 16, 2));
            if (port != 0)
            {
                peers.Add(new PeerEndpoint(address.ToString(), port));
            }
        }
    }

    private static void AddDistinct(List<PeerEndpoint> peers, IReadOnlyList<PeerEndpoint> trackerPeers)
    {
        for (int i = 0; i < trackerPeers.Count; i++)
        {
            PeerEndpoint candidate = trackerPeers[i];
            bool exists = false;
            for (int j = 0; j < peers.Count; j++)
            {
                if (peers[j].Equals(candidate))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                peers.Add(candidate);
            }
        }
    }
}

internal sealed record TrackerAnnounceResult(IReadOnlyList<PeerEndpoint> Peers, TimeSpan Interval);
