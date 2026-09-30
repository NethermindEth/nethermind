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
    [Test]
    public async Task Concurrent_piece_completion_cannot_make_snapshot_verified_bytes_exceed_received_bytes()
    {
        long received = 0;
        long verified = 0;
        using ManualResetEventSlim receivedRead = new();
        using ManualResetEventSlim pieceComplete = new();
        Task writer = Task.Run(() =>
        {
            if (!receivedRead.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Counter read did not begin.");
            }

            Interlocked.Add(ref received, 4);
            Interlocked.Add(ref verified, 4);
            pieceComplete.Set();
        });

        (long snapshotReceived, long snapshotVerified) = TorrentSession.ReadTransferCounters(
            () => Interlocked.Read(ref verified),
            () =>
            {
                long value = Interlocked.Read(ref received);
                receivedRead.Set();
                if (!pieceComplete.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Piece did not complete.");
                }

                return value;
            });
        await writer;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(snapshotReceived, Is.Zero);
            Assert.That(snapshotVerified, Is.Zero);
            Assert.That(snapshotVerified, Is.LessThanOrEqualTo(snapshotReceived));
        }
    }

    [Test]
    public void Seed_peer_pool_rotates_old_addresses_when_full()
    {
        HashSet<PeerEndpoint> peers = [];
        Queue<PeerEndpoint> order = [];
        Dictionary<PeerEndpoint, DateTimeOffset> retryAt = [];
        PeerEndpoint[] first = [.. Enumerable.Range(0, 512).Select(i => new PeerEndpoint($"peer{i}.example", 6881))];
        TorrentSession.AddSeedPeers(peers, order, retryAt, first);
        retryAt[first[0]] = DateTimeOffset.UtcNow.AddMinutes(2);
        PeerEndpoint newcomer = new("new-peer.example", 6881);

        TorrentSession.AddSeedPeers(peers, order, retryAt, [newcomer]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peers, Has.Count.EqualTo(512));
            Assert.That(peers, Does.Not.Contain(first[0]));
            Assert.That(peers, Does.Contain(newcomer));
            Assert.That(retryAt, Does.Not.ContainKey(first[0]));
            Assert.That(order.Peek(), Is.EqualTo(first[1]));
        }
    }

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
    public void Constructor_accepts_ephemeral_listen_port_when_seeding()
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            ListenPort = 0,
        };

        Assert.DoesNotThrow(() => new TorrentSession(options, _ => { }));
    }

    [TestCase(0)]
    [TestCase(-1)]
    [TestCase(65)]
    public void Constructor_rejects_invalid_upload_slots(int maxUploadPeers)
    {
        TorrentClientOptions options = new()
        {
            TorrentPath = "payload.torrent",
            OutputDirectory = "downloads",
            MaxUploadPeers = maxUploadPeers,
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
            SeedAfterCompletion = false,
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
            TaskCompletionSource resumePayload = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task peer = ServePayloadAsync(listener, infoHash, payload, timeout.Token, resumePayload.Task);
            TorrentClientOptions options = new()
            {
                TorrentPath = torrentPath,
                OutputDirectory = root,
                EnableTrackers = false,
                EnableDht = false,
                ExplicitPeers = [$"127.0.0.1:{port}"],
                MaxPeers = 1,
                VerifyExistingData = false,
                SeedAfterCompletion = false,
                PeerTimeout = TimeSpan.FromSeconds(3),
            };

            TorrentSession session = new(options, _ => { });
            Task<TorrentMetadata> download = session.RunAsync(timeout.Token);
            while (session.GetAvailabilitySnapshot()?.HasPeerInventory != true)
            {
                await Task.Delay(10, timeout.Token);
            }

            TorrentAvailabilitySnapshot connectedAvailability = session.GetAvailabilitySnapshot()!.Value;
            Assert.That(connectedAvailability.AvailablePieces, Is.EqualTo(new byte[] { 0b1000_0000 }));
            resumePayload.SetResult();
            await download;
            await peer;

            TorrentAvailabilitySnapshot finalAvailability = session.GetAvailabilitySnapshot()!.Value;

            TorrentTransferSnapshot transfer = session.GetTransferSnapshot();
            TorrentClientOptions verifiedOptions = new()
            {
                TorrentPath = torrentPath,
                OutputDirectory = root,
                EnableTrackers = false,
                EnableDht = false,
                ExplicitPeers = [$"127.0.0.1:{port}"],
                SeedAfterCompletion = false,
            };
            TorrentSession verificationOnly = new(verifiedOptions, _ => { });
            await verificationOnly.RunAsync(timeout.Token);

            using (Assert.EnterMultipleScope())
            {
                Assert.That(File.ReadAllBytes(Path.Combine(root, "data.bin")), Is.EqualTo(payload));
                Assert.That(transfer.PayloadBytesReceived, Is.EqualTo(payload.Length));
                Assert.That(transfer.VerifiedBytesFromPeers, Is.EqualTo(payload.Length));
                Assert.That(transfer.ContributingPeers, Is.EqualTo(1));
                Assert.That(transfer.ActiveTime, Is.GreaterThan(TimeSpan.Zero));
                Assert.That(verificationOnly.GetTransferSnapshot().PayloadBytesReceived, Is.Zero);
                Assert.That(verificationOnly.GetTransferSnapshot().VerifiedBytesFromPeers, Is.Zero);
                Assert.That(verificationOnly.GetTransferSnapshot().ContributingPeers, Is.Zero);
                Assert.That(finalAvailability.HasPeerInventory, Is.False);
                Assert.That(finalAvailability.AvailablePieces, Is.EqualTo(new byte[] { 0b1000_0000 }));
            }
        }
        finally
        {
            listener.Stop();
            File.Delete(Path.Combine(root, "data.bin"));
            File.Delete(torrentPath);
            Directory.Delete(root);
        }
    }

    [Test]
    public async Task Rejected_piece_counts_received_payload_but_not_verified_content_or_contributors()
    {
        byte[] expectedPayload = "data"u8.ToArray();
        byte[] info = [.. "d6:lengthi4e4:name8:data.bin12:piece lengthi4e6:pieces20:"u8.ToArray(),
            .. SHA1.HashData(expectedPayload), (byte)'e'];
        byte[] torrent = [(byte)'d', .. "4:info"u8.ToArray(), .. info, (byte)'e'];
        string root = Path.Combine(Path.GetTempPath(), "nethermind-bad-peer-" + Guid.NewGuid().ToString("N"));
        string torrentPath = Path.Combine(root, "source.torrent");
        Directory.CreateDirectory(root);
        TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await File.WriteAllBytesAsync(torrentPath, torrent);
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task peer = ServePayloadAsync(listener, SHA1.HashData(info), "oops"u8.ToArray(), timeout.Token);
            TorrentClientOptions options = new()
            {
                TorrentPath = torrentPath,
                OutputDirectory = root,
                EnableTrackers = false,
                EnableDht = false,
                ExplicitPeers = [$"127.0.0.1:{port}"],
                VerifyExistingData = false,
                SeedAfterCompletion = false,
            };
            TorrentSession session = new(options, message =>
            {
                if (message.StartsWith("peer ", StringComparison.Ordinal) && message.Contains(" failed:", StringComparison.Ordinal))
                {
                    timeout.Cancel();
                }
            });

            Assert.ThrowsAsync<OperationCanceledException>(() => session.RunAsync(timeout.Token));
            await peer;
            TorrentTransferSnapshot transfer = session.GetTransferSnapshot();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(transfer.PayloadBytesReceived, Is.EqualTo(4));
                Assert.That(transfer.VerifiedBytesFromPeers, Is.Zero);
                Assert.That(transfer.ContributingPeers, Is.Zero);
            }
        }
        finally
        {
            listener.Stop();
            File.Delete(Path.Combine(root, "data.bin"));
            File.Delete(torrentPath);
            Directory.Delete(root);
        }
    }

    private static async Task ServePayloadAsync(TcpListener listener, byte[] infoHash, byte[] payload, CancellationToken token,
        Task? resumePayload = null)
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
        if (resumePayload is not null) await resumePayload.WaitAsync(token);
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
