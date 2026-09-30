// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Nethermind.Torrent;

internal sealed class PeerSeedServer(TorrentMetadata torrent, byte[] peerId, PiecePicker picker, TorrentStorage storage,
    int listenPort, int maxUploadPeers, TimeSpan peerTimeout, Action<int> blockUploaded, Action<string> log,
    TimeSpan? uninterestedIdleTimeout = null, TimeSpan? interestedIdleTimeout = null) : IAsyncDisposable
{
    private const int HandshakeLength = 68;
    private const int MaxBlockLength = 16 * 1024;
    private const int MaxMessageLength = 64 * 1024;
    private static ReadOnlySpan<byte> ProtocolName => "BitTorrent protocol"u8;
    private static readonly byte[] UnchokeMessage = [0, 0, 0, 1, (byte)PeerMessageId.Unchoke];

    private readonly TorrentMetadata _torrent = torrent;
    private readonly byte[] _peerId = peerId;
    private readonly PiecePicker _picker = picker;
    private readonly TorrentStorage _storage = storage;
    private readonly Action<string> _log = log;
    private readonly Action<int> _blockUploaded = blockUploaded;
    private readonly TimeSpan _peerTimeout = peerTimeout;
    private readonly TimeSpan _uninterestedIdleTimeout = uninterestedIdleTimeout ?? TimeSpan.FromSeconds(30);
    private readonly TimeSpan _interestedIdleTimeout = interestedIdleTimeout ?? TimeSpan.FromMinutes(2);
    private TcpListener _listener = new(IPAddress.Any, listenPort);
    private readonly SemaphoreSlim _slots = new(maxUploadPeers, maxUploadPeers);
    private readonly SemaphoreSlim _outboundSlots = new(Math.Max(1, maxUploadPeers / 2), Math.Max(1, maxUploadPeers / 2));
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _peersLock = new();
    private readonly List<SeedPeer> _peers = [];
    private readonly HashSet<PeerEndpoint> _outboundEndpoints = [];
    private readonly List<Task> _outboundTasks = [];
    private Task? _acceptTask;

    private bool CanServeMetadata => _torrent.InfoBytes.Length is > 0 and <= MagnetMetadataProtocol.MaxMetadataSize;

    private sealed class SeedPeer(TcpClient client, CancellationTokenSource cancellation)
    {
        private readonly Lock _closeLock = new();
        private bool _closed;

        public TcpClient Client { get; } = client;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public ReadOnlyMemory<byte> RemoteReservedBits { get; set; }
        public byte RemoteMetadataId { get; set; }
        public SemaphoreSlim WriteGate { get; } = new(1, 1);
        public Channel<int> Have { get; } = Channel.CreateBounded<int>(new BoundedChannelOptions(128)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

        public void Close()
        {
            lock (_closeLock)
            {
                if (_closed)
                {
                    return;
                }

                _closed = true;
                Have.Writer.TryComplete();
                try
                {
                    Cancellation.Cancel();
                }
                finally
                {
                    Client.Dispose();
                }
            }
        }
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int ActivePeers
    {
        get
        {
            lock (_peersLock)
            {
                return _peers.Count;
            }
        }
    }

    public void Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException exception) when (listenPort != 0 &&
            exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            _listener.Stop();
            _log($"TCP port {listenPort} unavailable ({exception.SocketErrorCode}); selecting an available port");
            _listener = new TcpListener(IPAddress.Any, 0);
            _listener.Start();
        }

        _acceptTask = AcceptLoopAsync();
        _log($"listening for peers on TCP port {Port}");
    }

    public void PieceVerified(int pieceIndex)
    {
        SeedPeer[] peers;
        lock (_peersLock)
        {
            peers = [.. _peers];
        }

        for (int i = 0; i < peers.Length; i++)
        {
            if (!peers[i].Have.Writer.TryWrite(pieceIndex))
            {
                peers[i].Close();
            }
        }
    }

    public bool TryConnect(PeerEndpoint endpoint)
    {
        if (endpoint.Port is < 1 or > ushort.MaxValue)
        {
            return false;
        }

        lock (_peersLock)
        {
            if (_acceptTask is null || _stop.IsCancellationRequested || _outboundEndpoints.Contains(endpoint) || !_outboundSlots.Wait(0))
            {
                return false;
            }

            _outboundEndpoints.Add(endpoint);
            _outboundTasks.RemoveAll(static task => task.IsCompleted);
            try
            {
                _outboundTasks.Add(Task.Run(() => ConnectAndServeAsync(endpoint)));
            }
            catch
            {
                _outboundEndpoints.Remove(endpoint);
                _outboundSlots.Release();
                throw;
            }

            return true;
        }
    }

    private async Task AcceptLoopAsync()
    {
        List<Task> active = [];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!_slots.Wait(0))
                {
                    client.Dispose();
                    continue;
                }

                active.RemoveAll(static task => task.IsCompleted);
                active.Add(ServeInboundPeerAsync(client));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (SocketException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(active);
        }
    }

    private async Task ServeInboundPeerAsync(TcpClient client)
    {
        try
        {
            await ServePeerAsync(client, initiator: false);
        }
        finally
        {
            client.Dispose();
            _slots.Release();
        }
    }

    private async Task ConnectAndServeAsync(PeerEndpoint endpoint)
    {
        TcpClient? client = null;
        try
        {
            client = new TcpClient();
            client.NoDelay = true;
            using CancellationTokenSource connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(endpoint.Host, endpoint.Port, connectTimeout.Token);
            if (_slots.Wait(0))
            {
                try
                {
                    await ServePeerAsync(client, initiator: true);
                }
                finally
                {
                    _slots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"upload peer {endpoint} connection failed: {exception.Message}");
        }
        finally
        {
            client?.Dispose();
            lock (_peersLock)
            {
                _outboundEndpoints.Remove(endpoint);
            }

            _outboundSlots.Release();
        }
    }

    private async Task ServePeerAsync(TcpClient client, bool initiator)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        SeedPeer peer = new(client, cancellation);
        try
        {
            client.NoDelay = true;
            await using NetworkStream stream = client.GetStream();
            using (CancellationTokenSource handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token))
            {
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                peer.RemoteReservedBits = await ExchangeHandshakeAsync(stream, initiator, handshakeTimeout.Token);
            }

            lock (_peersLock)
            {
                _peers.Add(peer);
            }

            byte[] bitfield = _picker.GetCompletedBitfield();
            if (_picker.CompletedPieces != 0)
            {
                byte[] message = new byte[5 + bitfield.Length];
                BinaryPrimitives.WriteInt32BigEndian(message, 1 + bitfield.Length);
                message[4] = (byte)PeerMessageId.Bitfield;
                bitfield.CopyTo(message, 5);
                await WriteAsync(peer, stream, message, cancellation.Token);
            }

            bool supportsMetadata = CanServeMetadata && (peer.RemoteReservedBits.Span[5] & 0x10) != 0;
            if (supportsMetadata)
            {
                await SendExtendedHandshakeAsync(peer, stream, cancellation.Token);
            }

            Task haveSender = SendHavesAsync(peer, stream, cancellation.Token);
            try
            {
                bool interested = false;
                long lastRequestAt = Stopwatch.GetTimestamp();
                byte[] pieceMessage = new byte[13 + MaxBlockLength];
                while (!cancellation.IsCancellationRequested)
                {
                    TimeSpan idleLimit = interested ? _interestedIdleTimeout : _uninterestedIdleTimeout;
                    TimeSpan remaining = idleLimit - Stopwatch.GetElapsedTime(lastRequestAt);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    using CancellationTokenSource idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                    idleTimeout.CancelAfter(remaining);
                    PeerMessage message;
                    try
                    {
                        message = await ReadMessageAsync(stream, idleTimeout.Token);
                    }
                    catch (OperationCanceledException) when (idleTimeout.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    {
                        break;
                    }

                    switch (message.Id)
                    {
                        case PeerMessageId.Interested:
                            if (!interested)
                            {
                                interested = true;
                                await WriteAsync(peer, stream, UnchokeMessage, cancellation.Token);
                            }

                            break;
                        case PeerMessageId.NotInterested:
                            interested = false;
                            break;
                        case PeerMessageId.Request when interested:
                            await ServeRequestAsync(peer, stream, message.Payload, pieceMessage, cancellation.Token);
                            lastRequestAt = Stopwatch.GetTimestamp();
                            break;
                        case PeerMessageId.Extended when supportsMetadata:
                            if (message.Payload.Length > 0 && message.Payload.Span[0] == 0)
                            {
                                ReadRemoteExtensionHandshake(peer, message.Payload.Span[1..]);
                                break;
                            }

                            if (await ServeMetadataRequestAsync(peer, stream, message.Payload, cancellation.Token))
                            {
                                lastRequestAt = Stopwatch.GetTimestamp();
                            }

                            break;
                    }
                }
            }
            finally
            {
                peer.Close();
                await haveSender;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (EndOfStreamException)
        {
        }
        catch (IOException exception)
        {
            _log($"upload peer I/O ended: {exception.Message}");
        }
        catch (Exception exception)
        {
            _log($"upload peer rejected: {exception.Message}");
        }
        finally
        {
            lock (_peersLock)
            {
                _peers.Remove(peer);
            }

            peer.Close();
            peer.WriteGate.Dispose();
        }
    }

    private async Task<byte[]> ExchangeHandshakeAsync(NetworkStream stream, bool initiator, CancellationToken token)
    {
        if (initiator)
        {
            await stream.WriteAsync(CreateHandshake(), token);
        }

        byte[] handshake = new byte[HandshakeLength];
        await ReadExactlyAsync(stream, handshake, token);
        if (handshake[0] != ProtocolName.Length ||
            !handshake.AsSpan(1, ProtocolName.Length).SequenceEqual(ProtocolName) ||
            !handshake.AsSpan(28, TorrentMetadata.Sha1Length).SequenceEqual(_torrent.InfoHash) ||
            handshake.AsSpan(48, TorrentMetadata.Sha1Length).SequenceEqual(_peerId))
        {
            throw new InvalidDataException("Peer handshake does not match this torrent.");
        }

        if (!initiator)
        {
            await stream.WriteAsync(CreateHandshake(), token);
        }

        return handshake.AsSpan(20, 8).ToArray();
    }

    private byte[] CreateHandshake()
    {
        byte[] handshake = new byte[HandshakeLength];
        handshake[0] = (byte)ProtocolName.Length;
        ProtocolName.CopyTo(handshake.AsSpan(1));
        if (CanServeMetadata)
        {
            handshake[25] = 0x10;
        }

        _torrent.InfoHash.CopyTo(handshake.AsSpan(28, TorrentMetadata.Sha1Length));
        _peerId.CopyTo(handshake.AsSpan(48, TorrentMetadata.Sha1Length));
        return handshake;
    }

    private async Task SendExtendedHandshakeAsync(SeedPeer peer, NetworkStream stream, CancellationToken token)
    {
        BDictionary body = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("m", Bencode.Dictionary(
                new KeyValuePair<string, BValue>("ut_metadata", Bencode.Integer(MagnetMetadataProtocol.LocalMetadataId)))),
            new KeyValuePair<string, BValue>("metadata_size", Bencode.Integer(_torrent.InfoBytes.Length)));
        await WriteAsync(peer, stream, MagnetMetadataProtocol.CreateExtendedMessage(0, Bencode.Encode(body)), token);
    }

    private static void ReadRemoteExtensionHandshake(SeedPeer peer, ReadOnlySpan<byte> body)
    {
        if (body.Length > 16384)
        {
            throw new InvalidDataException("Peer extension handshake is too large.");
        }

        BDictionary handshake = BencodeDocument.Decode(body).Root.AsDictionary("extension handshake");
        if (handshake.TryGetValue("m", out BValue? mapping) && mapping is BDictionary extensions &&
            extensions.TryGetValue("ut_metadata", out BValue? metadataId) && metadataId is not null)
        {
            long id = metadataId.AsInteger("ut_metadata");
            peer.RemoteMetadataId = id is >= 1 and <= 255 ? (byte)id : (byte)0;
        }
    }

    private async Task<bool> ServeMetadataRequestAsync(SeedPeer peer, NetworkStream stream,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length is < 1 or > 1024)
        {
            throw new InvalidDataException("Peer metadata request has an invalid length.");
        }

        if (payload.Span[0] != MagnetMetadataProtocol.LocalMetadataId || peer.RemoteMetadataId == 0)
        {
            return false;
        }

        BDictionary request = BencodeDocument.Decode(payload.Span[1..]).Root.AsDictionary("metadata request");
        if (request["msg_type"].AsInteger("msg_type") != 0)
        {
            return false;
        }

        long piece = request["piece"].AsInteger("piece");
        int metadataSize = _torrent.InfoBytes.Length;
        int pieceCount = (metadataSize + MagnetMetadataProtocol.PieceSize - 1) / MagnetMetadataProtocol.PieceSize;
        bool validPiece = piece >= 0 && piece < pieceCount;
        BDictionary header = validPiece
            ? Bencode.Dictionary(
                new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(1)),
                new KeyValuePair<string, BValue>("piece", Bencode.Integer(piece)),
                new KeyValuePair<string, BValue>("total_size", Bencode.Integer(metadataSize)))
            : Bencode.Dictionary(
                new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(2)),
                new KeyValuePair<string, BValue>("piece", Bencode.Integer(piece)));
        byte[] headerBytes = Bencode.Encode(header);
        int offset = validPiece ? (int)piece * MagnetMetadataProtocol.PieceSize : 0;
        int pieceLength = validPiece ? Math.Min(MagnetMetadataProtocol.PieceSize, metadataSize - offset) : 0;
        byte[] body = new byte[headerBytes.Length + pieceLength];
        headerBytes.CopyTo(body, 0);
        if (validPiece)
        {
            _torrent.InfoBytes.Span.Slice(offset, pieceLength).CopyTo(body.AsSpan(headerBytes.Length));
        }

        await WriteAsync(peer, stream,
            MagnetMetadataProtocol.CreateExtendedMessage(peer.RemoteMetadataId, body), token);
        return true;
    }

    private async Task ServeRequestAsync(SeedPeer peer, NetworkStream stream, ReadOnlyMemory<byte> request,
        byte[] pieceMessage, CancellationToken token)
    {
        if (request.Length != 12)
        {
            throw new InvalidDataException("Peer request has an invalid length.");
        }

        int index = BinaryPrimitives.ReadInt32BigEndian(request.Span[..4]);
        int begin = BinaryPrimitives.ReadInt32BigEndian(request.Span.Slice(4, 4));
        int length = BinaryPrimitives.ReadInt32BigEndian(request.Span.Slice(8, 4));
        if ((uint)index >= (uint)_torrent.PieceCount || length is < 1 or > MaxBlockLength ||
            begin < 0 || begin > _torrent.GetPieceSize(index) - length || !_picker.IsPieceComplete(index))
        {
            throw new InvalidDataException("Peer requested an unavailable piece block.");
        }

        bool available = await _storage.ReadBlockAsync(index, begin, pieceMessage.AsMemory(13, length), token);
        if (!available)
        {
            throw new IOException("Verified piece data is no longer available on disk.");
        }

        BinaryPrimitives.WriteInt32BigEndian(pieceMessage, 9 + length);
        pieceMessage[4] = (byte)PeerMessageId.Piece;
        BinaryPrimitives.WriteInt32BigEndian(pieceMessage.AsSpan(5, 4), index);
        BinaryPrimitives.WriteInt32BigEndian(pieceMessage.AsSpan(9, 4), begin);
        await WriteAsync(peer, stream, pieceMessage.AsMemory(0, 13 + length), token);
        _blockUploaded(length);
    }

    private async Task SendHavesAsync(SeedPeer peer, NetworkStream stream, CancellationToken token)
    {
        try
        {
            await foreach (int pieceIndex in peer.Have.Reader.ReadAllAsync(token))
            {
                byte[] message = new byte[9];
                BinaryPrimitives.WriteInt32BigEndian(message, 5);
                message[4] = (byte)PeerMessageId.Have;
                BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(5, 4), pieceIndex);
                await WriteAsync(peer, stream, message, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _log($"upload peer notification failed: {exception.Message}");
            peer.Close();
        }
    }

    private async Task<PeerMessage> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_peerTimeout < TimeSpan.FromMinutes(3) ? TimeSpan.FromMinutes(3) : _peerTimeout);
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length is < 0 or > MaxMessageLength)
        {
            throw new InvalidDataException("Peer message is too large.");
        }

        if (length == 0)
        {
            return new PeerMessage(PeerMessageId.KeepAlive, ReadOnlyMemory<byte>.Empty);
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, timeout.Token);
        return new PeerMessage((PeerMessageId)payload[0], payload.AsMemory(1));
    }

    private async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> destination, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_peerTimeout);
        await stream.ReadExactlyAsync(destination, timeout.Token);
    }

    private async Task WriteAsync(SeedPeer peer, NetworkStream stream, ReadOnlyMemory<byte> message, CancellationToken token)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_peerTimeout);
        await peer.WriteGate.WaitAsync(timeout.Token);
        try
        {
            await stream.WriteAsync(message, timeout.Token);
        }
        finally
        {
            peer.WriteGate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        if (_acceptTask is not null)
        {
            await _acceptTask;
        }

        Task[] outboundTasks;
        lock (_peersLock)
        {
            outboundTasks = [.. _outboundTasks];
        }

        await Task.WhenAll(outboundTasks);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();

        _outboundSlots.Dispose();
        _slots.Dispose();
        _stop.Dispose();
    }
}
