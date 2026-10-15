// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

public abstract class LeanMessage : P2PMessage
{
    public override string Protocol => LeanProtocol.Code;
}

/// <summary><c>Status (0x00) = [1, chain_id, genesis_hash, profiles, max_object_bytes]</c>.</summary>
public sealed class LeanStatusMessage(UInt256 chainId, ValueHash256 genesisHash, ValueHash256[] profiles, ulong maxObjectBytes) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Status;
    public UInt256 ChainId { get; } = chainId;
    public ValueHash256 GenesisHash { get; } = genesisHash;
    public ValueHash256[] Profiles { get; } = profiles;
    public ulong MaxObjectBytes { get; } = maxObjectBytes;
}

/// <summary><c>AnnounceObjects (0x01) = [descriptor, ...]</c>, in strictly ascending object ID order.</summary>
public sealed class AnnounceObjectsMessage(LeanDescriptor[] descriptors) : LeanMessage
{
    public override int PacketType => LeanMessageCode.AnnounceObjects;
    public LeanDescriptor[] Descriptors { get; } = descriptors;
}

/// <summary><c>GetChunks (0x02) = [request_id, object_id, indices]</c>.</summary>
public sealed class GetChunksMessage(ulong requestId, ValueHash256 objectId, int[] indices) : LeanMessage
{
    public override int PacketType => LeanMessageCode.GetChunks;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public int[] Indices { get; } = indices;
}

/// <summary><c>Chunk (0x03) = [request_id, object_id, index, chunk_bytes]</c>.</summary>
public sealed class ChunkMessage(ulong requestId, ValueHash256 objectId, int index, ReadOnlyMemory<byte> data) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Chunk;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public int Index { get; } = index;
    public ReadOnlyMemory<byte> Data { get; } = data;
}

/// <summary><c>Complete (0x04) = [request_id, object_id, status]</c>.</summary>
public sealed class CompleteMessage(ulong requestId, ValueHash256 objectId, LeanCompleteStatus status) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Complete;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public LeanCompleteStatus Status { get; } = status;
}

/// <summary><c>Cancel (0x05) = [request_id]</c>.</summary>
public sealed class CancelMessage(ulong requestId) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Cancel;
    public ulong RequestId { get; } = requestId;
}

/// <summary>Shared strict framing: a size bound before any parsing, one outer list, and no trailing bytes.</summary>
public abstract class LeanMessageSerializer<T> : IZeroMessageSerializer<T> where T : LeanMessage
{
    public virtual void Serialize(IByteBuffer byteBuffer, T message)
    {
        byte[] encoded = Encode(message);
        if (encoded.Length > LeanProtocol.MaxMessageBytes) throw new ArgumentException($"{typeof(T).Name} exceeds its message size limit");
        byteBuffer.WriteBytes(encoded);
    }

    public T Deserialize(IByteBuffer byteBuffer)
    {
        int length = byteBuffer.ReadableBytes;
        if (length > LeanProtocol.MaxMessageBytes) throw new RlpException($"{typeof(T).Name} exceeds its message size limit");
        T message = Decode(LeanWire.Span(byteBuffer));
        byteBuffer.SkipBytes(length);
        return message;
    }

    /// <summary>Decodes message data, which must be exactly one canonical list.</summary>
    public T Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > LeanProtocol.MaxMessageBytes) throw new RlpException($"{typeof(T).Name} exceeds its message size limit");
        LeanRlpReader outer = new(data);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        T message = Decode(ref fields);
        fields.End();
        return message;
    }

    public abstract byte[] Encode(T message);

    protected abstract T Decode(ref LeanRlpReader fields);

    protected static ValueHash256[] ReadAscendingHashes(ref LeanRlpReader fields, int max)
    {
        LeanRlpReader list = fields.ReadList();
        int count = list.CountRemaining(max);
        if (count == 0) throw new RlpException("Empty hash list");
        ValueHash256[] hashes = new ValueHash256[count];
        for (int i = 0; i < count; i++)
        {
            hashes[i] = list.ReadHash();
            if (i > 0 && hashes[i - 1].Bytes.SequenceCompareTo(hashes[i].Bytes) >= 0)
                throw new RlpException("Hashes are not in strictly ascending order");
        }
        list.End();
        return hashes;
    }

    protected static byte[] EncodeHashes(ReadOnlySpan<ValueHash256> hashes)
    {
        byte[][] items = new byte[hashes.Length][];
        for (int i = 0; i < hashes.Length; i++) items[i] = LeanRlp.EncodeBytes(hashes[i].Bytes);
        return LeanRlp.EncodeList(items);
    }
}

internal static class LeanWire
{
    public static ReadOnlySpan<byte> Span(IByteBuffer buffer)
    {
        int length = buffer.ReadableBytes;
        if (buffer.HasArray) return buffer.Array.AsSpan(buffer.ArrayOffset + buffer.ReaderIndex, length);
        byte[] copy = new byte[length];
        buffer.GetBytes(buffer.ReaderIndex, copy);
        return copy;
    }
}

public sealed class LeanStatusMessageSerializer : LeanMessageSerializer<LeanStatusMessage>
{
    public override byte[] Encode(LeanStatusMessage message)
    {
        byte[][] profiles = new byte[message.Profiles.Length][];
        for (int i = 0; i < profiles.Length; i++) profiles[i] = LeanRlp.EncodeBytes(message.Profiles[i].Bytes);
        return LeanRlp.EncodeList(LeanRlp.EncodeUInt(LeanProtocol.StatusVersion), LeanRlp.EncodeUInt256(message.ChainId),
            LeanRlp.EncodeBytes(message.GenesisHash.Bytes), LeanRlp.EncodeList(profiles), LeanRlp.EncodeUInt(message.MaxObjectBytes));
    }

    protected override LeanStatusMessage Decode(ref LeanRlpReader fields)
    {
        if (fields.ReadUInt64() != LeanProtocol.StatusVersion) throw new RlpException("Unknown lean Status version");
        UInt256 chainId = fields.ReadUInt256();
        ValueHash256 genesis = fields.ReadHash();
        ValueHash256[] profiles = ReadAscendingHashes(ref fields, LeanProtocol.MaxProfiles);
        ulong maxObjectBytes = fields.ReadUInt64();
        if (maxObjectBytes is 0 or > LeanProtocol.MaxObjectBytes) throw new RlpException("Invalid max_object_bytes");
        return new(chainId, genesis, profiles, maxObjectBytes);
    }
}

public sealed class AnnounceObjectsMessageSerializer : LeanMessageSerializer<AnnounceObjectsMessage>
{
    public override byte[] Encode(AnnounceObjectsMessage message)
    {
        byte[][] items = new byte[message.Descriptors.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = message.Descriptors[i].Encoded;
        return LeanRlp.EncodeList(items);
    }

    protected override AnnounceObjectsMessage Decode(ref LeanRlpReader fields)
    {
        int count = fields.CountRemaining(LeanProtocol.MaxAnnouncements);
        if (count == 0) throw new RlpException("Empty announcement");
        LeanDescriptor[] descriptors = new LeanDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            descriptors[i] = LeanDescriptor.Decode(fields.ReadItem());
            if (i > 0 && descriptors[i - 1].ObjectId.CompareTo(descriptors[i].ObjectId) >= 0)
                throw new RlpException("Announcements are not in strictly ascending object ID order");
        }
        return new(descriptors);
    }
}

public sealed class GetChunksMessageSerializer : LeanMessageSerializer<GetChunksMessage>
{
    public override byte[] Encode(GetChunksMessage message)
    {
        byte[][] indices = new byte[message.Indices.Length][];
        for (int i = 0; i < indices.Length; i++) indices[i] = LeanRlp.EncodeUInt((ulong)message.Indices[i]);
        return LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), LeanRlp.EncodeBytes(message.ObjectId.Bytes), LeanRlp.EncodeList(indices));
    }

    protected override GetChunksMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        ValueHash256 objectId = fields.ReadHash();
        LeanRlpReader list = fields.ReadList();
        int count = list.CountRemaining(LeanProtocol.MaxChunksPerRequest);
        if (count == 0) throw new RlpException("Empty chunk index list");
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            uint index = list.ReadUInt32();
            if (index >= LeanProtocol.MaxChunkCount) throw new RlpException("Chunk index exceeds any object geometry");
            indices[i] = (int)index;
            if (i > 0 && indices[i - 1] >= indices[i]) throw new RlpException("Chunk indices are not strictly ascending");
        }
        list.End();
        return new(requestId, objectId, indices);
    }
}

/// <summary>Writes Chunk messages directly into the outgoing buffer; reception parses the packet in place.</summary>
public sealed class ChunkMessageSerializer : LeanMessageSerializer<ChunkMessage>
{

    public override void Serialize(IByteBuffer byteBuffer, ChunkMessage message) => SerializeCore(byteBuffer, message);

    public override byte[] Encode(ChunkMessage message)
    {
        IByteBuffer buffer = Unpooled.Buffer();
        try
        {
            SerializeCore(buffer, message);
            byte[] result = new byte[buffer.ReadableBytes];
            buffer.ReadBytes(result);
            return result;
        }
        finally { buffer.Release(); }
    }

    private static void SerializeCore(IByteBuffer buffer, ChunkMessage message)
    {
        int content = LeanRlp.LengthOfUInt(message.RequestId) + 33 + LeanRlp.LengthOfUInt((ulong)message.Index)
            + LeanRlp.LengthOfBytes(message.Data.Span);
        int total = LeanRlp.LengthOfList(content);
        if (total > LeanProtocol.MaxMessageBytes) throw new ArgumentException("Chunk exceeds MAX_MESSAGE_BYTES");
        buffer.EnsureWritable(total);
        Span<byte> header = stackalloc byte[64];
        int written = LeanRlp.WriteListPrefix(header, content);
        written += LeanRlp.WriteUInt(header[written..], message.RequestId);
        written += LeanRlp.WriteBytes(header[written..], message.ObjectId.Bytes);
        written += LeanRlp.WriteUInt(header[written..], (ulong)message.Index);
        buffer.WriteBytes(header[..written]);
        ReadOnlySpan<byte> data = message.Data.Span;
        if (data.Length == 1 && data[0] < 0x80) buffer.WriteByte(data[0]);
        else
        {
            written = LeanRlp.WriteStringPrefix(header, data.Length);
            buffer.WriteBytes(header[..written]);
            buffer.WriteBytes(data);
        }

    }

    protected override ChunkMessage Decode(ref LeanRlpReader fields)
    {
        LeanChunkView view = LeanChunkView.Read(ref fields);
        return new(view.RequestId, view.ObjectId, view.Index, view.Data.ToArray());
    }
}

/// <summary>A received Chunk whose bytes still alias the packet, so they are charged before any copy.</summary>
public readonly ref struct LeanChunkView
{
    public ulong RequestId { get; private init; }
    public ValueHash256 ObjectId { get; private init; }
    public int Index { get; private init; }
    public ReadOnlySpan<byte> Data { get; private init; }

    public static LeanChunkView Parse(ReadOnlySpan<byte> message)
    {
        if (message.Length > LeanProtocol.MaxMessageBytes) throw new RlpException("Chunk exceeds MAX_MESSAGE_BYTES");
        LeanRlpReader outer = new(message);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        LeanChunkView view = Read(ref fields);
        fields.End();
        return view;
    }

    internal static LeanChunkView Read(scoped ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        ValueHash256 objectId = fields.ReadHash();
        uint index = fields.ReadUInt32();
        if (index >= LeanProtocol.MaxChunkCount) throw new RlpException("Chunk index exceeds any object geometry");
        ReadOnlySpan<byte> data = fields.ReadBytes();
        if (data.Length is 0 or > LeanProtocol.ChunkBytes) throw new RlpException("Chunk length is out of bounds");
        return new LeanChunkView { RequestId = requestId, ObjectId = objectId, Index = (int)index, Data = data };
    }
}

public sealed class CompleteMessageSerializer : LeanMessageSerializer<CompleteMessage>
{
    public override byte[] Encode(CompleteMessage message) =>
        LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), LeanRlp.EncodeBytes(message.ObjectId.Bytes), LeanRlp.EncodeUInt((ulong)message.Status));

    protected override CompleteMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        ValueHash256 objectId = fields.ReadHash();
        byte status = fields.ReadUInt8();
        if (status > (byte)LeanCompleteStatus.TooLarge) throw new RlpException("Unknown Complete status");
        return new(requestId, objectId, (LeanCompleteStatus)status);
    }
}

public sealed class CancelMessageSerializer : LeanMessageSerializer<CancelMessage>
{
    public override byte[] Encode(CancelMessage message) => LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId));

    protected override CancelMessage Decode(ref LeanRlpReader fields) => new(fields.ReadUInt64());
}
