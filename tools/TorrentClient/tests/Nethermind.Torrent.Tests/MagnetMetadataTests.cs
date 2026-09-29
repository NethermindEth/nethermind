// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class MagnetMetadataTests
{
    private static readonly byte[] Info = CreateInfo();
    private static readonly byte[] InfoHash = SHA1.HashData(Info);
    private static readonly string HashHex = Convert.ToHexString(InfoHash).ToLowerInvariant();

    [Test]
    public void Parse_reads_btih_trackers_and_explicit_peers()
    {
        string tracker = "https://tracker.example/announce?key=a%2Bb";
        MagnetLink link = MagnetLink.Parse($"magnet:?xt=urn:btih:{HashHex.ToUpperInvariant()}&dn=Some+File&tr={Uri.EscapeDataString(tracker)}&tr={Uri.EscapeDataString(tracker)}&x.pe=127.0.0.1:6881&x.pe=%5B::1%5D:6882");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(link.InfoHashHex, Is.EqualTo(HashHex));
            Assert.That(link.DisplayName, Is.EqualTo("Some File"));
            Assert.That(link.Trackers, Has.Count.EqualTo(1));
            Assert.That(link.Trackers[0].AbsoluteUri, Is.EqualTo(tracker));
            Assert.That(link.Peers, Is.EquivalentTo(new[] { new PeerEndpoint("127.0.0.1", 6881), new PeerEndpoint("::1", 6882) }));
            Assert.That(link.ExplicitPeers, Is.EqualTo(new[] { "127.0.0.1:6881", "[::1]:6882" }));
        }
    }

    [Test]
    public void Parse_accepts_base32_btih()
    {
        string base32 = ToBase32(InfoHash);

        Assert.That(MagnetLink.Parse($"magnet:?xt=urn:btih:{base32.ToLowerInvariant()}").InfoHashHex, Is.EqualTo(HashHex));
    }

    [TestCase("magnet:?dn=missing")]
    [TestCase("magnet:?xt=urn:btih:bad")]
    [TestCase("magnet:?xt=urn:btmh:1220abcd")]
    [TestCase("https://example.org/?xt=urn:btih:0123456789012345678901234567890123456789")]
    public void Parse_rejects_invalid_or_non_v1_links(string uri)
        => Assert.That(() => MagnetLink.Parse(uri), Throws.TypeOf<FormatException>());

    [Test]
    public void Parse_rejects_conflicting_hashes()
        => Assert.That(() => MagnetLink.Parse($"magnet:?xt=urn:btih:{HashHex}&xt=urn:btih:{new string('0', 40)}"), Throws.TypeOf<FormatException>());

    [Test]
    public void ResolveAsync_honors_pre_canceled_token()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.That(async () => await MagnetMetadataResolver.ResolveAsync("not a magnet", new MagnetResolveOptions(), null, cancellation.Token),
            Throws.InstanceOf<OperationCanceledException>());
    }

    [Test]
    public void Handshake_sets_extension_bit_and_validates_hash()
    {
        byte[] handshake = MagnetMetadataProtocol.CreateHandshake(InfoHash, new byte[20]);

        Assert.DoesNotThrow(() => MagnetMetadataProtocol.ValidateHandshake(handshake, InfoHash));
        handshake[25] = 0;
        Assert.That(() => MagnetMetadataProtocol.ValidateHandshake(handshake, InfoHash), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(0)]
    [TestCase(MagnetMetadataProtocol.MaxMetadataSize + 1)]
    public void Extension_handshake_rejects_invalid_metadata_size(int size)
    {
        byte[] body = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("m", Bencode.Dictionary(new KeyValuePair<string, BValue>("ut_metadata", Bencode.Integer(3)))),
            new KeyValuePair<string, BValue>("metadata_size", Bencode.Integer(size))));

        Assert.That(() => MagnetMetadataProtocol.ParseExtendedHandshake(body), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Piece_parser_checks_index_size_and_rejection()
    {
        byte[] good = CreateData(1, MagnetMetadataProtocol.PieceSize + 1, [42]);
        Assert.That(MagnetMetadataProtocol.ParsePiece(good, 1, MagnetMetadataProtocol.PieceSize + 1), Is.EqualTo(new byte[] { 42 }));
        Assert.That(() => MagnetMetadataProtocol.ParsePiece(good, 0, MagnetMetadataProtocol.PieceSize + 1), Throws.TypeOf<InvalidDataException>());
        Assert.That(() => MagnetMetadataProtocol.ParsePiece(good, 1, MagnetMetadataProtocol.PieceSize + 2), Throws.TypeOf<InvalidDataException>());

        byte[] rejected = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(2)),
            new KeyValuePair<string, BValue>("piece", Bencode.Integer(1))));
        Assert.That(() => MagnetMetadataProtocol.ParsePiece(rejected, 1, MagnetMetadataProtocol.PieceSize + 1), Throws.TypeOf<InvalidDataException>());

        byte[] unknown = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(42))));
        Assert.That(MagnetMetadataProtocol.ParsePiece(unknown, 1, MagnetMetadataProtocol.PieceSize + 1), Is.Null);
    }

    [Test]
    public void Torrent_synthesis_preserves_exact_info_bytes_and_tracker_tiers()
    {
        Uri[] trackers = [new("https://one.example/announce"), new("udp://two.example:6969/announce")];
        byte[] torrent = MagnetMetadataProtocol.CreateTorrent(Info, InfoHash, trackers);
        BencodeDocument document = BencodeDocument.Decode(torrent);
        TorrentMetadata metadata = TorrentMetadata.Decode(torrent);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(document.InfoBytes, Is.EqualTo(Info));
            Assert.That(metadata.InfoHash, Is.EqualTo(InfoHash));
            Assert.That(metadata.Trackers, Is.EqualTo(trackers));
        }
        Assert.That(() => MagnetMetadataProtocol.CreateTorrent(Info, new byte[20], trackers), Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void Torrent_synthesis_rejects_noncanonical_info_dictionary()
    {
        byte[] noncanonical = [.. "d4:name1:x6:lengthi1e12:piece lengthi1e6:pieces20:"u8.ToArray(), .. new byte[20], (byte)'e'];
        byte[] hash = SHA1.HashData(noncanonical);

        Assert.That(() => MagnetMetadataProtocol.CreateTorrent(noncanonical, hash, []), Throws.TypeOf<FormatException>());
    }

    [Test]
    public void Torrent_synthesis_accepts_raw_byte_sorted_extension_keys()
    {
        byte[] canonical = [.. Info.AsSpan(0, Info.Length - 1).ToArray(), (byte)'3', (byte)':', 0xee, 0x80, 0x80,
            (byte)'i', (byte)'1', (byte)'e', (byte)'4', (byte)':', 0xf0, 0x90, 0x80, 0x80,
            (byte)'i', (byte)'2', (byte)'e', (byte)'e'];
        byte[] hash = SHA1.HashData(canonical);

        byte[] torrent = MagnetMetadataProtocol.CreateTorrent(canonical, hash, []);

        Assert.That(BencodeDocument.Decode(torrent).InfoBytes, Is.EqualTo(canonical));
    }

    [Test]
    public async Task ResolveAsync_downloads_metadata_from_explicit_peer()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServeMetadataAsync(listener, Info, InfoHash, timeout.Token);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromSeconds(3) };
            byte[] torrent = await MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{HashHex}&x.pe=127.0.0.1:{port}", options, null, timeout.Token);
            await peer;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(BencodeDocument.Decode(torrent).InfoBytes, Is.EqualTo(Info));
                Assert.That(TorrentMetadata.Decode(torrent).InfoHash, Is.EqualTo(InfoHash));
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_ignores_unknown_metadata_message_before_data()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServeMetadataAsync(listener, Info, InfoHash, timeout.Token, sendUnknownBeforeData: true);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromSeconds(3) };

            byte[] torrent = await MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{HashHex}&x.pe=127.0.0.1:{port}", options, null, timeout.Token);
            await peer;

            Assert.That(TorrentMetadata.Decode(torrent).InfoHash, Is.EqualTo(InfoHash));
        }
        finally
        {
            listener.Stop();
        }
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task ResolveAsync_stops_temporary_tracker_announce_after_metadata_transfer(bool validMetadata)
    {
        TcpListener peerListener = new(IPAddress.Loopback, 0);
        TcpListener trackerListener = new(IPAddress.Loopback, 0);
        peerListener.Start();
        trackerListener.Start();
        try
        {
            int peerPort = ((IPEndPoint)peerListener.LocalEndpoint).Port;
            int trackerPort = ((IPEndPoint)trackerListener.LocalEndpoint).Port;
            byte[] requestedHash = validMetadata ? InfoHash : new byte[20];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServeMetadataAsync(peerListener, Info, requestedHash, timeout.Token);
            Task<string[]> tracker = ServeTrackerAsync(trackerListener, peerPort, timeout.Token);
            MagnetResolveOptions options = new()
            {
                EnableDht = false,
                TrackerTimeout = TimeSpan.FromSeconds(3),
                PeerTimeout = TimeSpan.FromSeconds(3),
            };

            string magnet = $"magnet:?xt=urn:btih:{Convert.ToHexString(requestedHash)}&tr={Uri.EscapeDataString($"http://127.0.0.1:{trackerPort}/announce")}";
            if (validMetadata)
            {
                byte[] torrent = await MagnetMetadataResolver.ResolveAsync(magnet, options, null, timeout.Token);
                Assert.That(TorrentMetadata.Decode(torrent).InfoHash, Is.EqualTo(requestedHash));
            }
            else
            {
                Assert.That(async () => await MagnetMetadataResolver.ResolveAsync(magnet, options, null, timeout.Token),
                    Throws.TypeOf<InvalidOperationException>());
            }

            await peer;
            string[] requests = await tracker;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(requests[0], Does.Contain("event=started"));
                Assert.That(requests[1], Does.Contain("event=stopped"));
            }
        }
        finally
        {
            peerListener.Stop();
            trackerListener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_retries_untried_tracker_peer_after_empty_dht_lookup()
    {
        TcpListener trackerListener = new(IPAddress.Loopback, 0);
        trackerListener.Start();
        try
        {
            const int peerPort = 6881;
            int trackerPort = ((IPEndPoint)trackerListener.LocalEndpoint).Port;
            byte[] compactPeers = new byte[6 * 81];
            for (int i = 0; i < 81; i++)
            {
                compactPeers[i * 6] = 127;
                compactPeers[i * 6 + 3] = 1;
                int port = i == 80 ? peerPort : 10000 + i;
                compactPeers[i * 6 + 4] = (byte)(port >> 8);
                compactPeers[i * 6 + 5] = (byte)port;
            }

            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<string[]> tracker = ServeTrackerAsync(trackerListener, compactPeers, timeout.Token);
            MagnetResolveOptions options = new()
            {
                EnableDht = true,
                TrackerTimeout = TimeSpan.FromSeconds(3),
                DhtTimeout = TimeSpan.FromSeconds(1),
                PeerTimeout = TimeSpan.FromMilliseconds(500),
            };
            int dhtCalls = 0;
            int peerAttempts = 0;
            string magnet = $"magnet:?xt=urn:btih:{HashHex}&tr={Uri.EscapeDataString($"http://127.0.0.1:{trackerPort}/announce")}";

            byte[] torrent = await MagnetMetadataResolver.ResolveAsync(magnet, options, null, timeout.Token,
                (_, _) =>
                {
                    dhtCalls++;
                    return Task.FromResult<IReadOnlyList<PeerEndpoint>>([]);
                },
                (peer, _, _, _) =>
                {
                    peerAttempts++;
                    return peer.Port == peerPort
                        ? Task.FromResult(Info)
                        : Task.FromException<byte[]>(new IOException("Stale peer"));
                });
            await tracker;

            using (Assert.EnterMultipleScope())
            {
                Assert.That(dhtCalls, Is.EqualTo(1));
                Assert.That(peerAttempts, Is.EqualTo(81));
                Assert.That(TorrentMetadata.Decode(torrent).InfoHash, Is.EqualTo(InfoHash));
            }
        }
        finally
        {
            trackerListener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_downloads_multiple_metadata_pieces()
    {
        byte[] info = CreateInfo(820);
        byte[] hash = SHA1.HashData(info);
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServeMetadataAsync(listener, info, hash, timeout.Token);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromSeconds(3) };
            byte[] torrent = await MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{Convert.ToHexString(hash)}&x.pe=127.0.0.1:{port}", options, null, timeout.Token);
            await peer;

            Assert.That(BencodeDocument.Decode(torrent).InfoBytes, Is.EqualTo(info));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_rejects_metadata_with_wrong_infohash()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            byte[] requestedHash = new byte[20];
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServeMetadataAsync(listener, Info, requestedHash, timeout.Token);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromSeconds(3) };
            InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{Convert.ToHexString(requestedHash)}&x.pe=127.0.0.1:{port}", options, null, timeout.Token));
            await peer;
            Assert.That(error?.Message, Does.Contain("SHA-1 does not match"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_bounds_idle_peer_time()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }, timeout.Token);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromMilliseconds(200) };

            InvalidOperationException? error = Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{HashHex}&x.pe=127.0.0.1:{port}", options, null, timeout.Token));
            await peer;
            Assert.That(error?.Message, Does.Contain("timed out"));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Test]
    public async Task ResolveAsync_cancels_direct_peer_transfer()
    {
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = Task.Run(async () =>
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(timeout.Token);
                connected.SetResult();
                await Task.Delay(TimeSpan.FromMilliseconds(500), timeout.Token);
            }, timeout.Token);
            MagnetResolveOptions options = new() { EnableTrackers = false, EnableDht = false, PeerTimeout = TimeSpan.FromSeconds(3) };
            Task<byte[]> resolution = MagnetMetadataResolver.ResolveAsync($"magnet:?xt=urn:btih:{HashHex}&x.pe=127.0.0.1:{port}", options, null, cancellation.Token);
            await connected.Task.WaitAsync(timeout.Token);
            await cancellation.CancelAsync();

            Assert.That(async () => await resolution, Throws.InstanceOf<OperationCanceledException>());
            await peer;
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task ServeMetadataAsync(TcpListener listener, byte[] info, byte[] requestedHash, CancellationToken token, bool sendUnknownBeforeData = false)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(token);
        await using NetworkStream stream = client.GetStream();
        byte[] handshake = new byte[68];
        await stream.ReadExactlyAsync(handshake, token);
        MagnetMetadataProtocol.ValidateHandshake(handshake, requestedHash);
        await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(requestedHash, new byte[20]), token);
        byte[] clientExtension = await ReadFrameAsync(stream, token);
        Assert.That(clientExtension[0..2], Is.EqualTo(new byte[] { 20, 0 }));

        byte[] extension = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("m", Bencode.Dictionary(new KeyValuePair<string, BValue>("ut_metadata", Bencode.Integer(3)))),
            new KeyValuePair<string, BValue>("metadata_size", Bencode.Integer(info.Length))));
        await stream.WriteAsync(MagnetMetadataProtocol.CreateExtendedMessage(0, extension), token);
        int pieceCount = (info.Length + MagnetMetadataProtocol.PieceSize - 1) / MagnetMetadataProtocol.PieceSize;
        for (int piece = 0; piece < pieceCount; piece++)
        {
            byte[] request = await ReadFrameAsync(stream, token);
            Assert.That(request[0..2], Is.EqualTo(new byte[] { 20, 3 }));
            BDictionary requestBody = BencodeDocument.Decode(request.AsSpan(2)).Root.AsDictionary("request");
            Assert.That(requestBody["piece"].AsInteger("piece"), Is.EqualTo(piece));
            if (sendUnknownBeforeData)
            {
                byte[] unknown = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(42))));
                await stream.WriteAsync(MagnetMetadataProtocol.CreateExtendedMessage(1, unknown), token);
            }

            byte[] block = info.AsSpan(piece * MagnetMetadataProtocol.PieceSize, Math.Min(MagnetMetadataProtocol.PieceSize, info.Length - piece * MagnetMetadataProtocol.PieceSize)).ToArray();
            byte[] response = CreateData(piece, info.Length, block);
            await stream.WriteAsync(MagnetMetadataProtocol.CreateExtendedMessage(1, response), token);
        }
    }

    private static Task<string[]> ServeTrackerAsync(TcpListener listener, int peerPort, CancellationToken token)
    {
        byte[] peer = [127, 0, 0, 1, (byte)(peerPort >> 8), (byte)peerPort];
        return ServeTrackerAsync(listener, peer, token);
    }

    private static async Task<string[]> ServeTrackerAsync(TcpListener listener, byte[] peers, CancellationToken token)
    {
        byte[] response = Bencode.Encode(Bencode.Dictionary(new KeyValuePair<string, BValue>("peers", Bencode.Bytes(peers))));
        string[] requests = new string[2];
        for (int i = 0; i < requests.Length; i++)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = client.GetStream();
            using MemoryStream request = new();
            byte[] buffer = new byte[1024];
            while (!Encoding.ASCII.GetString(request.ToArray()).Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int count = await stream.ReadAsync(buffer, token);
                if (count == 0 || request.Length + count > 8192)
                {
                    throw new InvalidDataException("Unexpected local tracker request.");
                }

                request.Write(buffer, 0, count);
            }

            requests[i] = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n", 2)[0];
            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, token);
            await stream.WriteAsync(response, token);
        }

        return requests;
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, token);
        byte[] frame = new byte[BinaryPrimitives.ReadInt32BigEndian(length)];
        await stream.ReadExactlyAsync(frame, token);
        return frame;
    }

    private static byte[] CreateData(int piece, int totalSize, byte[] block)
    {
        byte[] header = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("piece", Bencode.Integer(piece)),
            new KeyValuePair<string, BValue>("total_size", Bencode.Integer(totalSize))));
        return [.. header, .. block];
    }

    private static byte[] CreateInfo(int pieceCount = 1)
    {
        BDictionary info = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("length", Bencode.Integer(pieceCount)),
            new KeyValuePair<string, BValue>("name", Bencode.String("x")),
            new KeyValuePair<string, BValue>("piece length", Bencode.Integer(1)),
            new KeyValuePair<string, BValue>("pieces", Bencode.Bytes(new byte[20 * pieceCount])));
        return Bencode.Encode(info);
    }

    private static string ToBase32(ReadOnlySpan<byte> data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        StringBuilder result = new();
        int bits = 0;
        int accumulator = 0;
        foreach (byte value in data)
        {
            accumulator = (accumulator << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                result.Append(alphabet[(accumulator >> bits) & 31]);
                accumulator &= (1 << bits) - 1;
            }
        }

        return result.ToString();
    }
}
