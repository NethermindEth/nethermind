// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;

namespace Nethermind.Torrent.Tests;

[TestFixture]
public sealed class PeerSeedServerTests
{
    [Test]
    public async Task Completed_session_uploads_verified_payload_until_stopped()
    {
        byte[] payload = "data"u8.ToArray();
        using TestTorrent fixture = await TestTorrent.CreateAsync(payload);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        TorrentSession session = new(new TorrentClientOptions
        {
            TorrentPath = fixture.TorrentPath,
            OutputDirectory = fixture.Root,
            ListenPort = 0,
            EnableTrackers = false,
            EnableDht = false,
        }, _ => { });

        Task<TorrentMetadata> running = session.RunAsync(cancellation.Token);
        try
        {
            int port = await WaitForPortAsync(session, cancellation.Token);
            using TcpClient client = await ConnectAsync(port, fixture.InfoHash, cancellation.Token);
            NetworkStream stream = client.GetStream();
            byte[] bitfield = new byte[6];
            await stream.ReadExactlyAsync(bitfield, cancellation.Token);
            Assert.That(bitfield, Is.EqualTo(new byte[] { 0, 0, 0, 2, (byte)PeerMessageId.Bitfield, 0x80 }));

            await InterestAsync(stream, cancellation.Token);
            await RequestAsync(stream, index: 0, begin: 0, payload.Length, cancellation.Token);
            byte[] piece = new byte[13 + payload.Length];
            await stream.ReadExactlyAsync(piece, cancellation.Token);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(piece[4], Is.EqualTo((byte)PeerMessageId.Piece));
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(piece.AsSpan(5, 4)), Is.Zero);
                Assert.That(BinaryPrimitives.ReadInt32BigEndian(piece.AsSpan(9, 4)), Is.Zero);
                Assert.That(piece.AsSpan(13).ToArray(), Is.EqualTo(payload));
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.GetTransferSnapshot().UploadedBytes, Is.EqualTo(payload.Length));
            Assert.That(session.ListeningPort, Is.Zero);
        }
    }

    [Test]
    public async Task Unavailable_listen_port_falls_back_and_advertises_actual_port()
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        using TcpListener occupied = new(IPAddress.Any, 0);
        occupied.Start();
        int requestedPort = ((IPEndPoint)occupied.LocalEndpoint).Port;
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        TorrentSession session = new(new TorrentClientOptions
        {
            TorrentPath = fixture.TorrentPath,
            OutputDirectory = fixture.Root,
            ListenPort = requestedPort,
            EnableTrackers = false,
            EnableDht = false,
        }, _ => { });

        Task<TorrentMetadata> running = session.RunAsync(cancellation.Token);
        try
        {
            int actualPort = await WaitForPortAsync(session, cancellation.Token);
            Assert.That(actualPort, Is.Not.EqualTo(requestedPort));
            using TcpClient client = await ConnectAsync(actualPort, fixture.InfoHash, cancellation.Token);
            Assert.That(client.Connected, Is.True);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Test]
    public async Task Private_torrent_never_uses_public_dht_as_its_only_discovery_source()
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray(), isPrivate: true);
        Assert.That(TorrentMetadata.Load(fixture.TorrentPath).IsPrivate, Is.True);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Root, "data.bin"), "oops"u8.ToArray());
        TorrentSession session = new(new TorrentClientOptions
        {
            TorrentPath = fixture.TorrentPath,
            OutputDirectory = fixture.Root,
            ListenPort = 0,
            EnableTrackers = false,
            EnableDht = true,
            ExplicitPeers = ["127.0.0.1:6881"],
        }, _ => { });

        Assert.That(async () => await session.RunAsync(CancellationToken.None),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains("Incomplete payload"));
    }

    [Test]
    public async Task Restored_complete_seed_does_not_report_a_new_tracker_completion()
    {
        using TcpListener tracker = new(IPAddress.Loopback, 0);
        tracker.Start();
        Uri trackerUri = new($"http://127.0.0.1:{((IPEndPoint)tracker.LocalEndpoint).Port}/announce");
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray(), tracker: trackerUri);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        using CancellationTokenSource seeding = new();
        List<string> requests = [];
        Task server = ObserveTrackerAsync(tracker, requests, timeout.Token);
        TaskCompletionSource seedingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress<TorrentSessionProgress> progress = new(snapshot =>
        {
            if (snapshot.Phase == TorrentSessionPhase.Seeding)
            {
                seedingStarted.TrySetResult();
            }
        });
        TorrentSession session = new(new TorrentClientOptions
        {
            TorrentPath = fixture.TorrentPath,
            OutputDirectory = fixture.Root,
            ListenPort = 0,
            EnableDht = false,
        }, _ => { }, progress);

        Task<TorrentMetadata> running = session.RunAsync(seeding.Token);
        try
        {
            await seedingStarted.Task.WaitAsync(timeout.Token);
        }
        finally
        {
            await seeding.CancelAsync();
            await running;
        }

        await server;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requests, Has.Count.EqualTo(2));
            Assert.That(requests[0], Does.Contain("event=started"));
            Assert.That(requests[1], Does.Contain("event=stopped"));
            Assert.That(requests, Has.None.Contains("event=completed"));
        }
    }

    [Test]
    public async Task Stopping_seed_closes_listener_before_waiting_for_tracker()
    {
        using TcpListener tracker = new(IPAddress.Loopback, 0);
        tracker.Start();
        Uri trackerUri = new($"http://127.0.0.1:{((IPEndPoint)tracker.LocalEndpoint).Port}/announce");
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray(), tracker: trackerUri);
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using CancellationTokenSource seeding = new();
        List<string> requests = [];
        TaskCompletionSource stoppedReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task server = ObserveTrackerAsync(tracker, requests, timeout.Token, stoppedReceived, TimeSpan.FromSeconds(1));
        TaskCompletionSource seedingStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Progress<TorrentSessionProgress> progress = new(snapshot =>
        {
            if (snapshot.Phase == TorrentSessionPhase.Seeding)
            {
                seedingStarted.TrySetResult();
            }
        });
        TorrentSession session = new(new TorrentClientOptions
        {
            TorrentPath = fixture.TorrentPath,
            OutputDirectory = fixture.Root,
            ListenPort = 0,
            EnableDht = false,
            TrackerTimeout = TimeSpan.FromMilliseconds(250),
        }, _ => { }, progress);

        Task<TorrentMetadata> running = session.RunAsync(seeding.Token);
        await seedingStarted.Task.WaitAsync(timeout.Token);
        int port = session.ListeningPort;
        await seeding.CancelAsync();
        await stoppedReceived.Task.WaitAsync(timeout.Token);

        Assert.That(session.ListeningPort, Is.Zero);
        using TcpClient probe = new();
        Assert.That(async () => await probe.ConnectAsync(IPAddress.Loopback, port, timeout.Token),
            Throws.TypeOf<SocketException>());
        await running.WaitAsync(timeout.Token);
        await server;
    }

    [Test]
    public async Task Unverified_piece_is_never_uploaded()
    {
        byte[] payload = "data"u8.ToArray();
        using TestTorrent fixture = await TestTorrent.CreateAsync(payload);
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        int uploaded = 0;
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), length => uploaded += length, _ => { });
        server.Start();

        using TcpClient client = await ConnectAsync(server.Port, fixture.InfoHash, cancellation.Token);
        NetworkStream stream = client.GetStream();
        await InterestAsync(stream, cancellation.Token);
        await RequestAsync(stream, index: 0, begin: 0, payload.Length, cancellation.Token);
        byte[] response = new byte[1];
        Assert.That(await stream.ReadAsync(response, cancellation.Token), Is.Zero);
        Assert.That(uploaded, Is.Zero);
    }

    [Test]
    public async Task Newly_verified_piece_is_announced_with_have_and_can_be_uploaded()
    {
        byte[] payload = "data"u8.ToArray();
        using TestTorrent fixture = await TestTorrent.CreateAsync(payload);
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), _ => { }, _ => { });
        server.Start();

        using TcpClient client = await ConnectAsync(server.Port, fixture.InfoHash, cancellation.Token);
        NetworkStream stream = client.GetStream();
        await InterestAsync(stream, cancellation.Token);
        picker.MarkComplete(0);
        server.PieceVerified(0);
        byte[] have = new byte[9];
        await stream.ReadExactlyAsync(have, cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(have[4], Is.EqualTo((byte)PeerMessageId.Have));
            Assert.That(BinaryPrimitives.ReadInt32BigEndian(have.AsSpan(5, 4)), Is.Zero);
        }

        await RequestAsync(stream, index: 0, begin: 0, payload.Length, cancellation.Token);
        byte[] piece = new byte[13 + payload.Length];
        await stream.ReadExactlyAsync(piece, cancellation.Token);
        Assert.That(piece.AsSpan(13).ToArray(), Is.EqualTo(payload));
    }

    [Test]
    public async Task Seed_supplies_original_metadata_to_a_magnet_peer()
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        picker.MarkComplete(0);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), _ => { }, _ => { });
        server.Start();

        using TcpClient client = await ConnectAsync(server.Port, fixture.InfoHash, cancellation.Token);
        NetworkStream stream = client.GetStream();
        byte[] bitfield = await ReadFrameAsync(stream, cancellation.Token);
        Assert.That(bitfield[0], Is.EqualTo((byte)PeerMessageId.Bitfield));
        byte[] extended = await ReadFrameAsync(stream, cancellation.Token);
        Assert.That(extended.AsSpan(0, 2).ToArray(), Is.EqualTo(new byte[] { (byte)PeerMessageId.Extended, 0 }));
        (byte metadataId, int metadataSize) = MagnetMetadataProtocol.ParseExtendedHandshake(extended.AsSpan(2));
        Assert.That(metadataSize, Is.EqualTo(metadata.InfoBytes.Length));

        BDictionary remoteHandshake = Bencode.Dictionary(new KeyValuePair<string, BValue>("m",
            Bencode.Dictionary(new KeyValuePair<string, BValue>("ut_metadata", Bencode.Integer(7)))));
        await stream.WriteAsync(MagnetMetadataProtocol.CreateExtendedMessage(0, Bencode.Encode(remoteHandshake)), cancellation.Token);
        await stream.WriteAsync(MagnetMetadataProtocol.CreateRequest(metadataId, 0), cancellation.Token);
        byte[] response = await ReadFrameAsync(stream, cancellation.Token);
        Assert.That(response.AsSpan(0, 2).ToArray(),
            Is.EqualTo(new byte[] { (byte)PeerMessageId.Extended, 7 }));
        Assert.That(MagnetMetadataProtocol.ParsePiece(response.AsSpan(2), 0, metadataSize),
            Is.EqualTo(metadata.InfoBytes.ToArray()));

        await stream.WriteAsync(MagnetMetadataProtocol.CreateRequest(metadataId, 99), cancellation.Token);
        byte[] rejection = await ReadFrameAsync(stream, cancellation.Token);
        Assert.That(rejection[1], Is.EqualTo(7));
        BDictionary rejected = BencodeDocument.Decode(rejection.AsSpan(2)).Root.AsDictionary("metadata rejection");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rejected["msg_type"].AsInteger("msg_type"), Is.EqualTo(2));
            Assert.That(rejected["piece"].AsInteger("piece"), Is.EqualTo(99));
        }
    }

    [Test]
    public async Task Outbound_seed_uploads_a_piece_to_a_reachable_leecher()
    {
        byte[] payload = "data"u8.ToArray();
        using TestTorrent fixture = await TestTorrent.CreateAsync(payload);
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        picker.MarkComplete(0);
        TaskCompletionSource<int> uploaded = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), length => uploaded.TrySetResult(length), _ => { });
        server.Start();

        using TcpListener leecher = new(IPAddress.Loopback, 0);
        leecher.Start();
        PeerEndpoint endpoint = new(IPAddress.Loopback.ToString(), ((IPEndPoint)leecher.LocalEndpoint).Port);
        Assert.That(server.TryConnect(endpoint), Is.True);
        Assert.That(server.TryConnect(endpoint), Is.False, "an active endpoint must not be connected twice");
        int otherPort = endpoint.Port == ushort.MaxValue ? endpoint.Port - 1 : endpoint.Port + 1;
        Assert.That(server.TryConnect(new PeerEndpoint(endpoint.Host, otherPort)), Is.False,
            "a second endpoint must not exceed the upload slot limit");

        using TcpClient client = await leecher.AcceptTcpClientAsync(cancellation.Token);
        NetworkStream stream = client.GetStream();
        byte[] handshake = new byte[68];
        await stream.ReadExactlyAsync(handshake, cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(handshake[0], Is.EqualTo(19));
            Assert.That(handshake.AsSpan(1, 19).ToArray(), Is.EqualTo("BitTorrent protocol"u8.ToArray()));
            Assert.That(handshake.AsSpan(28, 20).ToArray(), Is.EqualTo(fixture.InfoHash));
            Assert.That(handshake.AsSpan(48, 20).ToArray(), Is.EqualTo(new byte[20]));
        }

        await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(fixture.InfoHash,
            [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]), cancellation.Token);
        byte[] bitfield = new byte[6];
        await stream.ReadExactlyAsync(bitfield, cancellation.Token);
        Assert.That(bitfield, Is.EqualTo(new byte[] { 0, 0, 0, 2, (byte)PeerMessageId.Bitfield, 0x80 }));
        Assert.That(GetRemoteReservedBits(server).Span[5] & 0x10, Is.EqualTo(0x10));

        await InterestAsync(stream, cancellation.Token);
        await RequestAsync(stream, index: 0, begin: 0, payload.Length, cancellation.Token);
        byte[] piece = new byte[13 + payload.Length];
        await stream.ReadExactlyAsync(piece, cancellation.Token);
        int uploadedLength = await uploaded.Task.WaitAsync(cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(piece[4], Is.EqualTo((byte)PeerMessageId.Piece));
            Assert.That(piece.AsSpan(13).ToArray(), Is.EqualTo(payload));
            Assert.That(uploadedLength, Is.EqualTo(payload.Length));
        }
    }

    [Test]
    public async Task Pending_outbound_handshake_preserves_an_inbound_upload_slot()
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        picker.MarkComplete(0);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 2,
            TimeSpan.FromSeconds(3), _ => { }, _ => { });
        server.Start();

        using TcpListener leecher = new(IPAddress.Loopback, 0);
        leecher.Start();
        PeerEndpoint endpoint = new(IPAddress.Loopback.ToString(), ((IPEndPoint)leecher.LocalEndpoint).Port);
        Assert.That(server.TryConnect(endpoint), Is.True);
        using TcpClient pendingOutbound = await leecher.AcceptTcpClientAsync(cancellation.Token);
        byte[] handshake = new byte[68];
        await pendingOutbound.GetStream().ReadExactlyAsync(handshake, cancellation.Token);

        using TcpClient inbound = await ConnectAsync(server.Port, fixture.InfoHash, cancellation.Token);
        byte[] bitfield = await ReadFrameAsync(inbound.GetStream(), cancellation.Token);
        Assert.That(bitfield[0], Is.EqualTo((byte)PeerMessageId.Bitfield));
    }

    [Test]
    public async Task Outbound_seed_rejects_invalid_handshake([Values] bool selfPeerId)
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        picker.MarkComplete(0);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), _ => { }, _ => { });
        server.Start();

        using TcpListener leecher = new(IPAddress.Loopback, 0);
        leecher.Start();
        PeerEndpoint endpoint = new(IPAddress.Loopback.ToString(), ((IPEndPoint)leecher.LocalEndpoint).Port);
        Assert.That(server.TryConnect(endpoint), Is.True);
        using TcpClient client = await leecher.AcceptTcpClientAsync(cancellation.Token);
        NetworkStream stream = client.GetStream();
        byte[] outboundHandshake = new byte[68];
        await stream.ReadExactlyAsync(outboundHandshake, cancellation.Token);
        byte[] infoHash = selfPeerId ? fixture.InfoHash : new byte[20];
        byte[] peerId = selfPeerId ? new byte[20] : Enumerable.Repeat((byte)1, 20).ToArray();
        await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(infoHash, peerId), cancellation.Token);

        byte[] response = new byte[1];
        int read = await stream.ReadAsync(response, cancellation.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(read, Is.Zero);
            Assert.That(server.ActivePeers, Is.Zero);
        }
    }

    [Test]
    public async Task Idle_peer_is_closed_despite_keepalives([Values] bool interested)
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        await using PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), _ => { }, _ => { },
            uninterestedIdleTimeout: TimeSpan.FromMilliseconds(500), interestedIdleTimeout: TimeSpan.FromMilliseconds(500));
        server.Start();

        using TcpClient client = await ConnectAsync(server.Port, fixture.InfoHash, cancellation.Token);
        NetworkStream stream = client.GetStream();
        if (interested)
        {
            await InterestAsync(stream, cancellation.Token);
        }
        else
        {
            byte[] extended = await ReadFrameAsync(stream, cancellation.Token);
            Assert.That(extended[0], Is.EqualTo((byte)PeerMessageId.Extended));
        }

        using CancellationTokenSource keepalives = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task sender = SendKeepalivesAsync(stream, started, keepalives.Token);
        try
        {
            await started.Task.WaitAsync(cancellation.Token);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            byte[] response = new byte[1];
            bool closed;
            try
            {
                closed = await stream.ReadAsync(response, deadline.Token) == 0;
            }
            catch (IOException exception) when (exception.InnerException is SocketException
            { SocketErrorCode: SocketError.ConnectionReset })
            {
                closed = true;
            }

            Assert.That(closed, Is.True);
        }
        finally
        {
            await keepalives.CancelAsync();
            await sender;
        }
    }

    [Test]
    public async Task Dispose_cancels_pending_outbound_handshake()
    {
        using TestTorrent fixture = await TestTorrent.CreateAsync("data"u8.ToArray());
        TorrentMetadata metadata = TorrentMetadata.Load(fixture.TorrentPath);
        await using TorrentStorage storage = new(metadata, fixture.Root);
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        await storage.InitializeAsync(cancellation.Token);
        PiecePicker picker = new(metadata);
        using TcpListener leecher = new(IPAddress.Loopback, 0);
        leecher.Start();
        PeerEndpoint endpoint = new(IPAddress.Loopback.ToString(), ((IPEndPoint)leecher.LocalEndpoint).Port);
        PeerSeedServer server = new(metadata, new byte[20], picker, storage, 0, 1,
            TimeSpan.FromSeconds(3), _ => { }, _ => { });
        TcpClient? client = null;
        try
        {
            await using (server)
            {
                server.Start();
                Assert.That(server.TryConnect(endpoint), Is.True);
                client = await leecher.AcceptTcpClientAsync(cancellation.Token);
                byte[] handshake = new byte[68];
                await client.GetStream().ReadExactlyAsync(handshake, cancellation.Token);
            }

            byte[] response = new byte[1];
            int read = await client.GetStream().ReadAsync(response, cancellation.Token);
            bool connectedAfterDispose = server.TryConnect(endpoint);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(read, Is.Zero);
                Assert.That(connectedAfterDispose, Is.False);
            }
        }
        finally
        {
            client?.Dispose();
        }
    }

    [Test]
    public async Task Concurrent_close_waits_for_in_progress_cancellation()
    {
        Type peerType = typeof(PeerSeedServer).GetNestedType("SeedPeer", BindingFlags.NonPublic)!;
        MethodInfo close = peerType.GetMethod("Close")!;
        using TcpClient client = new();
        using CancellationTokenSource cancellation = new();
        object peer = Activator.CreateInstance(peerType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null, args: [client, cancellation], culture: null)!;
        using ManualResetEventSlim enteredCancellation = new();
        using ManualResetEventSlim releaseCancellation = new();
        using CancellationTokenRegistration registration = cancellation.Token.Register(() =>
        {
            enteredCancellation.Set();
            releaseCancellation.Wait(TimeSpan.FromSeconds(10));
        });

        Task first = Task.Run(() => close.Invoke(peer, null));
        Task? second = null;
        try
        {
            Assert.That(enteredCancellation.Wait(TimeSpan.FromSeconds(5)), Is.True);
            TaskCompletionSource secondStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
            second = Task.Run(() =>
            {
                secondStarted.SetResult();
                close.Invoke(peer, null);
            });
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.That(second.IsCompleted, Is.False);
        }
        finally
        {
            releaseCancellation.Set();
            await first;
            if (second is not null)
            {
                await second;
            }
        }
    }

    private static async Task<int> WaitForPortAsync(TorrentSession session, CancellationToken token)
    {
        while (session.ListeningPort == 0)
        {
            await Task.Delay(10, token);
        }

        return session.ListeningPort;
    }

    private static ReadOnlyMemory<byte> GetRemoteReservedBits(PeerSeedServer server)
    {
        FieldInfo peersField = typeof(PeerSeedServer).GetField("_peers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        object peer = ((System.Collections.IEnumerable)peersField.GetValue(server)!).Cast<object>().Single();
        PropertyInfo reservedBits = peer.GetType().GetProperty("RemoteReservedBits")!;
        return (ReadOnlyMemory<byte>)reservedBits.GetValue(peer)!;
    }

    private static async Task<TcpClient> ConnectAsync(int port, byte[] infoHash, CancellationToken token)
    {
        TcpClient client = new();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, port, token);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(MagnetMetadataProtocol.CreateHandshake(infoHash, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10,
                11, 12, 13, 14, 15, 16, 17, 18, 19, 20]), token);
            byte[] handshake = new byte[68];
            await stream.ReadExactlyAsync(handshake, token);
            Assert.That(handshake.AsSpan(28, 20).ToArray(), Is.EqualTo(infoHash));
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task InterestAsync(NetworkStream stream, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 0, 0, 0, 1, (byte)PeerMessageId.Interested }, token);
        for (int i = 0; i < 3; i++)
        {
            byte[] message = await ReadFrameAsync(stream, token);
            if (message[0] == (byte)PeerMessageId.Unchoke)
            {
                return;
            }

            Assert.That(message[0], Is.EqualTo((byte)PeerMessageId.Extended));
        }

        Assert.Fail("Upload peer did not unchoke the interested client.");
    }

    private static async Task<byte[]> ReadFrameAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] lengthBytes = new byte[4];
        await stream.ReadExactlyAsync(lengthBytes, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBytes);
        Assert.That(length, Is.InRange(1, 64 * 1024));
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return payload;
    }

    private static async Task SendKeepalivesAsync(NetworkStream stream, TaskCompletionSource started, CancellationToken token)
    {
        try
        {
            while (true)
            {
                await stream.WriteAsync(new byte[4], token);
                started.TrySetResult();
                await Task.Delay(30, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
        }
    }

    private static async Task RequestAsync(NetworkStream stream, int index, int begin, int length, CancellationToken token)
    {
        byte[] request = new byte[17];
        BinaryPrimitives.WriteInt32BigEndian(request, 13);
        request[4] = (byte)PeerMessageId.Request;
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(5, 4), index);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(9, 4), begin);
        BinaryPrimitives.WriteInt32BigEndian(request.AsSpan(13, 4), length);
        await stream.WriteAsync(request, token);
    }

    private static async Task ObserveTrackerAsync(TcpListener listener, List<string> requests, CancellationToken token,
        TaskCompletionSource? stoppedReceived = null, TimeSpan? stoppedDelay = null)
    {
        byte[] response = Bencode.Encode(Bencode.Dictionary(
            new KeyValuePair<string, BValue>("interval", Bencode.Integer(60)),
            new KeyValuePair<string, BValue>("peers", Bencode.Bytes([]))));
        for (int i = 0; i < 2; i++)
        {
            using TcpClient client = await listener.AcceptTcpClientAsync(token);
            await using NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, Encoding.ASCII, detectEncodingFromByteOrderMarks: false,
                bufferSize: 1024, leaveOpen: true);
            string request = await reader.ReadLineAsync(token) ?? throw new EndOfStreamException();
            requests.Add(request);
            for (int line = 0; line < 64; line++)
            {
                if (string.IsNullOrEmpty(await reader.ReadLineAsync(token)))
                {
                    break;
                }
            }

            if (i == 1)
            {
                stoppedReceived?.TrySetResult();
                if (stoppedDelay is TimeSpan delay)
                {
                    await Task.Delay(delay, token);
                }
            }

            byte[] header = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n");
            try
            {
                await stream.WriteAsync(header, token);
                await stream.WriteAsync(response, token);
            }
            catch (IOException) when (stoppedDelay is not null && i == 1)
            {
            }
        }
    }

    private sealed class TestTorrent : IDisposable
    {
        private TestTorrent(string root, byte[] infoHash)
        {
            Root = root;
            InfoHash = infoHash;
        }

        public string Root { get; }
        public string TorrentPath => Path.Combine(Root, "source.torrent");
        public byte[] InfoHash { get; }

        public static async Task<TestTorrent> CreateAsync(byte[] payload, bool isPrivate = false, Uri? tracker = null)
        {
            byte[] privateField = isPrivate ? "7:privatei1e"u8.ToArray() : [];
            byte[] info = [.. Encoding.ASCII.GetBytes($"d6:lengthi{payload.Length}e4:name8:data.bin12:piece lengthi4e6:pieces20:"),
                .. SHA1.HashData(payload), .. privateField, (byte)'e'];
            byte[] trackerField = tracker is null ? [] : Encoding.ASCII.GetBytes($"8:announce{tracker.AbsoluteUri.Length}:{tracker.AbsoluteUri}");
            byte[] torrent = [(byte)'d', .. trackerField, .. "4:info"u8.ToArray(), .. info, (byte)'e'];
            string root = Path.Combine(Path.GetTempPath(), "nethermind-seed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            TestTorrent fixture = new(root, SHA1.HashData(info));
            await File.WriteAllBytesAsync(fixture.TorrentPath, torrent);
            await File.WriteAllBytesAsync(Path.Combine(root, "data.bin"), payload);
            return fixture;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
