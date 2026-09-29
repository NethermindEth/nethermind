// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Nethermind.Torrent;

internal static class MagnetMetadataProtocol
{
    internal const int PieceSize = 16 * 1024;
    internal const int MaxMetadataSize = 8 * 1024 * 1024;
    internal const int MaxMessageSize = 2 * 1024 * 1024;
    internal const byte LocalMetadataId = 1;
    private static readonly byte[] ProtocolName = "BitTorrent protocol"u8.ToArray();

    internal static byte[] CreateHandshake(ReadOnlySpan<byte> infoHash, ReadOnlySpan<byte> peerId)
    {
        byte[] handshake = new byte[68];
        handshake[0] = (byte)ProtocolName.Length;
        ProtocolName.CopyTo(handshake.AsSpan(1));
        handshake[25] = 0x10;
        infoHash.CopyTo(handshake.AsSpan(28, 20));
        peerId.CopyTo(handshake.AsSpan(48, 20));
        return handshake;
    }

    internal static void ValidateHandshake(ReadOnlySpan<byte> handshake, ReadOnlySpan<byte> infoHash)
    {
        if (handshake.Length != 68 || handshake[0] != ProtocolName.Length ||
            !handshake.Slice(1, ProtocolName.Length).SequenceEqual(ProtocolName) ||
            (handshake[25] & 0x10) == 0 || !handshake.Slice(28, 20).SequenceEqual(infoHash))
        {
            throw new InvalidDataException("Peer handshake does not support BEP 10 for the requested infohash.");
        }
    }

    internal static byte[] CreateExtendedHandshake()
    {
        BDictionary body = Bencode.Dictionary(new KeyValuePair<string, BValue>("m", Bencode.Dictionary(
            new KeyValuePair<string, BValue>("ut_metadata", Bencode.Integer(LocalMetadataId)))));
        return CreateExtendedMessage(0, Bencode.Encode(body));
    }

    internal static byte[] CreateRequest(byte remoteMetadataId, int piece)
    {
        BDictionary body = Bencode.Dictionary(
            new KeyValuePair<string, BValue>("msg_type", Bencode.Integer(0)),
            new KeyValuePair<string, BValue>("piece", Bencode.Integer(piece)));
        return CreateExtendedMessage(remoteMetadataId, Bencode.Encode(body));
    }

    internal static byte[] CreateExtendedMessage(byte extensionId, ReadOnlySpan<byte> body)
    {
        byte[] message = new byte[checked(body.Length + 6)];
        BinaryPrimitives.WriteInt32BigEndian(message, body.Length + 2);
        message[4] = (byte)PeerMessageId.Extended;
        message[5] = extensionId;
        body.CopyTo(message.AsSpan(6));
        return message;
    }

    internal static (byte Id, int Size) ParseExtendedHandshake(ReadOnlySpan<byte> body)
    {
        if (body.Length > 16384)
        {
            throw new InvalidDataException("Peer extension handshake is too large.");
        }

        BDictionary handshake = BencodeDocument.Decode(body).Root.AsDictionary("extension handshake");
        BDictionary extensions = handshake["m"].AsDictionary("extension handshake.m");
        long id = extensions["ut_metadata"].AsInteger("ut_metadata");
        long size = handshake["metadata_size"].AsInteger("metadata_size");
        if (id is < 1 or > 255 || size is < 1 or > MaxMetadataSize)
        {
            throw new InvalidDataException($"Peer metadata extension ID or size is out of bounds (size {size}).");
        }

        return ((byte)id, (int)size);
    }

    internal static byte[]? ParsePiece(ReadOnlySpan<byte> body, int expectedPiece, int metadataSize)
    {
        if (body.Length > PieceSize + 1024)
        {
            throw new InvalidDataException("Metadata piece message is too large.");
        }

        BencodeParser parser = new(body);
        BDictionary header = parser.ParseValue(0).AsDictionary("metadata response");
        long messageType = header["msg_type"].AsInteger("msg_type");
        if (messageType is not (1 or 2))
        {
            return null;
        }

        long piece = header["piece"].AsInteger("piece");
        if (piece != expectedPiece)
        {
            throw new InvalidDataException($"Peer sent metadata piece {piece}, expected {expectedPiece}.");
        }

        if (messageType == 2)
        {
            throw new InvalidDataException($"Peer rejected metadata piece {piece}.");
        }

        if (header["total_size"].AsInteger("total_size") != metadataSize)
        {
            throw new InvalidDataException("Peer sent an invalid metadata response or inconsistent total size.");
        }

        int expectedLength = Math.Min(PieceSize, metadataSize - expectedPiece * PieceSize);
        if (expectedLength <= 0 || body.Length - parser.Position != expectedLength)
        {
            throw new InvalidDataException($"Peer sent an invalid length for metadata piece {piece}.");
        }

        return body[parser.Position..].ToArray();
    }

    internal static byte[] CreateTorrent(ReadOnlySpan<byte> infoBytes, ReadOnlySpan<byte> expectedHash, IReadOnlyList<Uri> trackers)
    {
        if (infoBytes.Length is < 1 or > MaxMetadataSize ||
            !SHA1.HashData(infoBytes).AsSpan().SequenceEqual(expectedHash))
        {
            throw new InvalidDataException("Metadata SHA-1 does not match the magnet infohash.");
        }

        BencodeDocument info = BencodeDocument.Decode(infoBytes, requireCanonical: true);
        _ = info.Root.AsDictionary("info");
        using MemoryStream stream = new();
        stream.WriteByte((byte)'d');
        if (trackers.Count > 0)
        {
            WriteValue(stream, "announce");
            WriteValue(stream, trackers[0].AbsoluteUri);
            if (trackers.Count > 1)
            {
                WriteValue(stream, "announce-list");
                stream.WriteByte((byte)'l');
                for (int i = 0; i < trackers.Count; i++)
                {
                    stream.WriteByte((byte)'l');
                    WriteValue(stream, trackers[i].AbsoluteUri);
                    stream.WriteByte((byte)'e');
                }

                stream.WriteByte((byte)'e');
            }
        }

        WriteValue(stream, "info");
        stream.Write(infoBytes);
        stream.WriteByte((byte)'e');
        byte[] torrent = stream.ToArray();
        TorrentMetadata decoded = TorrentMetadata.Decode(torrent);
        if (!decoded.InfoHash.AsSpan().SequenceEqual(expectedHash))
        {
            throw new InvalidDataException("Synthesized torrent changed the infohash.");
        }

        return torrent;
    }

    private static void WriteValue(Stream stream, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        byte[] prefix = Encoding.ASCII.GetBytes(bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":");
        stream.Write(prefix);
        stream.Write(bytes);
    }
}
