// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class TorrentSessionTests
{
    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(513)]
    public void Constructor_rejects_invalid_max_peers(int maxPeers)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            MaxPeers = maxPeers,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65536)]
    public void Constructor_rejects_invalid_listen_port(int listenPort)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            ListenPort = listenPort,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void Constructor_rejects_session_without_peer_discovery()
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            EnableDht = false,
            EnableTrackers = false,
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentException>());
    }

    [Test]
    public async Task Explicit_peer_can_supply_payload_without_trackers_or_dht()
    {
        byte[] payload = "data"u8.ToArray();
        byte[] info = [.. "d6:lengthi4e4:name8:data.bin12:piece lengthi4e6:pieces20:"u8.ToArray(),
            .. SHA1.HashData(payload), (byte)'e'];
        byte[] infoHash = SHA1.HashData(info);
        byte[] torrent = [(byte)'d', .. "4:info"u8.ToArray(), .. info, (byte)'e'];
        string root = Path.Combine(Path.GetTempPath(), "nethermind-direct-peer-" + Guid.NewGuid().ToString("N"));
        string torrentPath = Path.Combine(root, "source.torrent");
        Directory.CreateDirectory(root);
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await File.WriteAllBytesAsync(torrentPath, torrent);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServePayloadAsync(listener, infoHash, payload, timeout.Token);
            TorrentClientOptions options = new()
            {
                TorrentPath = torrentPath,
                OutputDirectory = root,
                EnableTrackers = false,
                EnableDht = false,
                ExplicitPeers = [$"127.0.0.1:{port}"],
                MaxPeers = 1,
                VerifyExistingData = false,
                PeerTimeout = TimeSpan.FromSeconds(3),
            };

            await new TorrentSession(options, _ => { }).RunAsync(timeout.Token);
            await peer;

            Assert.That(File.ReadAllBytes(Path.Combine(root, "data.bin")), Is.EqualTo(payload));
        }
        finally
        {
            listener.Stop();
            File.Delete(Path.Combine(root, "data.bin"));
            File.Delete(torrentPath);
            Directory.Delete(root);
        }
    }

    private static async Task ServePayloadAsync(TcpListener listener, byte[] infoHash, byte[] payload, CancellationToken token)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(token);
        await using NetworkStream stream = client.GetStream();
        byte[] handshake = new byte[68];
        await stream.ReadExactlyAsync(handshake, token);
        Assert.That(handshake.AsSpan(28, 20).ToArray(), Is.EqualTo(infoHash));
        await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(infoHash, new byte[20]), token);
        byte[] interested = new byte[5];
        await stream.ReadExactlyAsync(interested, token);
        Assert.That(interested[4], Is.EqualTo((byte)PeerMessageId.Interested));
        byte[] bitfield = [0, 0, 0, 2, (byte)PeerMessageId.Bitfield, 0x80];
        byte[] unchoke = [0, 0, 0, 1, (byte)PeerMessageId.Unchoke];
        await stream.WriteAsync(bitfield, token);
        await stream.WriteAsync(unchoke, token);
        byte[] request = new byte[17];
        await stream.ReadExactlyAsync(request, token);
        Assert.That(request[4], Is.EqualTo((byte)PeerMessageId.Request));
        Assert.That(BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(13, 4)), Is.EqualTo(payload.Length));
        byte[] piece = new byte[13 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(piece, 9 + payload.Length);
        piece[4] = (byte)PeerMessageId.Piece;
        payload.CopyTo(piece.AsSpan(13));
        await stream.WriteAsync(piece, token);
    }

    [TestCase("tracker", 0)]
    [TestCase("tracker", 3601)]
    [TestCase("dht-timeout", 0)]
    [TestCase("dht-timeout", 3601)]
    [TestCase("dht-interval", 0)]
    [TestCase("dht-interval", 3601)]
    [TestCase("peer", 0)]
    [TestCase("peer", 3601)]
    public void Constructor_rejects_invalid_timeout_options(string option, int seconds)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
        };
        TimeSpan timeout = TimeSpan.FromSeconds(seconds);
        options = option switch
        {
            "tracker" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                TrackerTimeout = timeout,
            },
            "dht-timeout" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                DhtLookupTimeout = timeout,
            },
            "dht-interval" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                DhtLookupInterval = timeout,
            },
            "peer" => new TorrentClientOptions
            {
                TorrentPath = options.TorrentPath,
                OutputDirectory = options.OutputDirectory,
                PeerTimeout = timeout,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(option), option, null),
        };

        Assert.That(() => new TorrentSession(options, _ => { }), Throws.TypeOf<ArgumentOutOfRangeException>());
    }
}
