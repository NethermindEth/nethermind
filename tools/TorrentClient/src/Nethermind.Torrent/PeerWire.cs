// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Torrent;

internal sealed class PeerWireClient(
    TorrentMetadata torrent,
    byte[] peerId,
    PiecePicker piecePicker,
    TorrentStorage storage,
    Action<string> log,
    TimeSpan peerTimeout,
    Action<int, PeerEndpoint>? pieceCompleted = null)
{
    private const int HandshakeLength = 68;
    private const string ProtocolName = "BitTorrent protocol";
    private const int BlockSize = 16 * 1024;
    private const int PipelineDepth = 8;
    private readonly TorrentMetadata _torrent = torrent;
    private readonly byte[] _peerId = peerId;
    private readonly PiecePicker _piecePicker = piecePicker;
    private readonly TorrentStorage _storage = storage;
    private readonly Action<string> _log = log;
    private readonly TimeSpan _peerTimeout = peerTimeout;
    private readonly Action<int, PeerEndpoint>? _pieceCompleted = pieceCompleted;

    public async Task RunPeerAsync(PeerEndpoint peer, CancellationToken token)
    {
        using TcpClient client = new();
        client.NoDelay = true;
        using CancellationTokenSource connectCts = CancellationTokenSource.CreateLinkedTokenSource(token);
        connectCts.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(peer.Host, peer.Port, connectCts.Token);
        await using NetworkStream stream = client.GetStream();

        await SendHandshakeAsync(stream, token);
        await ReceiveHandshakeAsync(stream, peer, token);
        await SendInterestedAsync(stream, token);

        PeerBitfield peerPieces = new(_torrent.PieceCount);
        bool choked = true;
        DateTimeOffset lastProgress = DateTimeOffset.UtcNow;

        while (!_piecePicker.IsComplete && !token.IsCancellationRequested)
        {
            if (choked || peerPieces.IsEmpty)
            {
                PeerMessage message = await ReadMessageAsync(stream, token);
                ProcessControlMessage(message, peerPieces, ref choked);
                ThrowIfIdle(peer, choked, peerPieces.IsEmpty, lastProgress);
                continue;
            }

            if (!_piecePicker.TryReserve(peerPieces, out int pieceIndex))
            {
                PeerMessage message = await ReadMessageAsync(stream, token);
                ProcessControlMessage(message, peerPieces, ref choked);
                if (!_piecePicker.HasReservablePiece(peerPieces))
                {
                    ThrowIfIdle(peer, choked, peerPieces.IsEmpty, lastProgress);
                }

                continue;
            }

            try
            {
                byte[] piece = await DownloadPieceAsync(stream, peer, peerPieces, pieceIndex, token);
                byte[] hash = new byte[TorrentMetadata.Sha1Length];
                SHA1.HashData(piece, hash);
                if (!hash.SequenceEqual(_torrent.GetPieceHash(pieceIndex)))
                {
                    throw new InvalidDataException($"Piece {pieceIndex} failed SHA-1 validation from {peer}.");
                }

                await _storage.WritePieceAsync(pieceIndex, piece, token);
                _piecePicker.MarkComplete(pieceIndex);
                _pieceCompleted?.Invoke(pieceIndex, peer);
                lastProgress = DateTimeOffset.UtcNow;
                _log($"piece {pieceIndex + 1}/{_torrent.PieceCount} from {peer}; {FormatPercent(_piecePicker.DownloadedBytes, _torrent.TotalLength)}");
            }
            catch
            {
                _piecePicker.Release(pieceIndex);
                throw;
            }
        }
    }

    private async Task<byte[]> DownloadPieceAsync(
        NetworkStream stream,
        PeerEndpoint peer,
        PeerBitfield peerPieces,
        int pieceIndex,
        CancellationToken token)
    {
        int pieceSize = _torrent.GetPieceSize(pieceIndex);
        byte[] piece = GC.AllocateUninitializedArray<byte>(pieceSize);
        Dictionary<int, int> outstandingRequests = [];
        int nextRequestOffset = 0;
        int inFlight = 0;
        int completedBytes = 0;
        DateTimeOffset lastBlockProgress = DateTimeOffset.UtcNow;

        while (completedBytes < pieceSize)
        {
            while (inFlight < PipelineDepth && nextRequestOffset < pieceSize)
            {
                int requestLength = Math.Min(BlockSize, pieceSize - nextRequestOffset);
                await SendRequestAsync(stream, pieceIndex, nextRequestOffset, requestLength, token);
                outstandingRequests[nextRequestOffset] = requestLength;
                nextRequestOffset += requestLength;
                inFlight++;
            }

            PeerMessage message = await ReadMessageAsync(stream, token);
            switch (message.Id)
            {
                case PeerMessageId.Piece:
                    if (message.Payload.Length < 8)
                    {
                        throw new InvalidDataException("Piece message is too short.");
                    }

                    int receivedPiece = BinaryPrimitives.ReadInt32BigEndian(message.Payload.Span[..4]);
                    int begin = BinaryPrimitives.ReadInt32BigEndian(message.Payload.Span.Slice(4, 4));
                    ReadOnlyMemory<byte> block = message.Payload[8..];
                    if (receivedPiece != pieceIndex ||
                        begin < 0 ||
                        begin + block.Length > pieceSize ||
                        !outstandingRequests.TryGetValue(begin, out int expectedLength) ||
                        expectedLength != block.Length)
                    {
                        throw new InvalidDataException($"Unexpected piece block from {peer}.");
                    }

                    outstandingRequests.Remove(begin);
                    block.CopyTo(piece.AsMemory(begin, block.Length));
                    completedBytes += block.Length;
                    lastBlockProgress = DateTimeOffset.UtcNow;

                    inFlight--;
                    break;
                case PeerMessageId.Choke:
                    throw new InvalidOperationException($"Peer {peer} choked during piece {pieceIndex}.");
                default:
                    bool ignoredChoked = false;
                    ProcessControlMessage(message, peerPieces, ref ignoredChoked);
                    ThrowIfPieceStalled(peer, pieceIndex, lastBlockProgress);
                    break;
            }

        }

        return piece;
    }

    private static string FormatPercent(long downloaded, long total)
    {
        if (total == 0)
        {
            return "100.00%";
        }

        double percent = downloaded * 100.0 / total;
        return percent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    private void ThrowIfIdle(PeerEndpoint peer, bool choked, bool hasNoPieces, DateTimeOffset lastProgress)
    {
        if (DateTimeOffset.UtcNow - lastProgress <= _peerTimeout)
        {
            return;
        }

        if (choked)
        {
            throw new TimeoutException($"Peer {peer} stayed choked too long.");
        }

        if (hasNoPieces)
        {
            throw new TimeoutException($"Peer {peer} did not advertise any pieces.");
        }

        throw new TimeoutException($"Peer {peer} has no useful pieces available.");
    }

    private void ThrowIfPieceStalled(PeerEndpoint peer, int pieceIndex, DateTimeOffset lastBlockProgress)
    {
        if (DateTimeOffset.UtcNow - lastBlockProgress > _peerTimeout)
        {
            throw new TimeoutException($"Peer {peer} did not send requested blocks for piece {pieceIndex}.");
        }
    }

    private static void ProcessControlMessage(PeerMessage message, PeerBitfield peerPieces, ref bool choked)
    {
        switch (message.Id)
        {
            case PeerMessageId.KeepAlive:
                break;
            case PeerMessageId.Choke:
                choked = true;
                break;
            case PeerMessageId.Unchoke:
                choked = false;
                break;
            case PeerMessageId.Bitfield:
                peerPieces.ReadBitfield(message.Payload.Span);
                break;
            case PeerMessageId.Have:
                if (message.Payload.Length == 4)
                {
                    peerPieces.SetPiece(BinaryPrimitives.ReadInt32BigEndian(message.Payload.Span));
                }

                break;
        }
    }

    private async Task SendHandshakeAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] handshake = new byte[HandshakeLength];
        handshake[0] = (byte)ProtocolName.Length;
        Encoding.ASCII.GetBytes(ProtocolName, handshake.AsSpan(1, ProtocolName.Length));
        _torrent.InfoHash.CopyTo(handshake.AsSpan(28, TorrentMetadata.Sha1Length));
        _peerId.CopyTo(handshake.AsSpan(48, TorrentMetadata.Sha1Length));
        await stream.WriteAsync(handshake, token);
    }

    private async Task ReceiveHandshakeAsync(NetworkStream stream, PeerEndpoint peer, CancellationToken token)
    {
        byte[] handshake = new byte[HandshakeLength];
        await ReadExactlyAsync(stream, handshake, token);
        if (handshake[0] != ProtocolName.Length ||
            !handshake.AsSpan(1, ProtocolName.Length).SequenceEqual(Encoding.ASCII.GetBytes(ProtocolName)) ||
            !handshake.AsSpan(28, TorrentMetadata.Sha1Length).SequenceEqual(_torrent.InfoHash))
        {
            throw new InvalidDataException($"Invalid BitTorrent handshake from {peer}.");
        }
    }

    private static async Task SendInterestedAsync(NetworkStream stream, CancellationToken token)
        => await SendMessageAsync(stream, PeerMessageId.Interested, ReadOnlyMemory<byte>.Empty, token);

    private static async Task SendRequestAsync(NetworkStream stream, int pieceIndex, int begin, int length, CancellationToken token)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0, 4), pieceIndex);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4, 4), begin);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8, 4), length);
        await SendMessageAsync(stream, PeerMessageId.Request, payload, token);
    }

    private static async Task SendMessageAsync(NetworkStream stream, PeerMessageId id, ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        byte[] header = new byte[5];
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(0, 4), payload.Length + 1);
        header[4] = (byte)id;
        await stream.WriteAsync(header, token);
        if (payload.Length != 0)
        {
            await stream.WriteAsync(payload, token);
        }
    }

    private async Task<PeerMessage> ReadMessageAsync(NetworkStream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await ReadExactlyAsync(stream, header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length == 0)
        {
            return new PeerMessage(PeerMessageId.KeepAlive, ReadOnlyMemory<byte>.Empty);
        }

        if (length < 0 || length > 2 * 1024 * 1024)
        {
            throw new InvalidDataException($"Invalid peer message length {length}.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, token);
        PeerMessageId id = (PeerMessageId)payload[0];
        return new PeerMessage(id, payload.AsMemory(1));
    }

    private async Task ReadExactlyAsync(NetworkStream stream, Memory<byte> destination, CancellationToken token)
    {
        int readTotal = 0;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(_peerTimeout);
        try
        {
            while (readTotal < destination.Length)
            {
                int read = await stream.ReadAsync(destination[readTotal..], timeout.Token);
                if (read == 0)
                {
                    throw new EndOfStreamException("Peer closed the connection.");
                }

                readTotal += read;
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException($"Peer read timed out after {_peerTimeout.TotalSeconds:0} seconds.");
        }
    }
}

internal readonly record struct PeerMessage(PeerMessageId Id, ReadOnlyMemory<byte> Payload);

internal enum PeerMessageId : byte
{
    KeepAlive = 255,
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,
    Port = 9,
    Extended = 20,
}
