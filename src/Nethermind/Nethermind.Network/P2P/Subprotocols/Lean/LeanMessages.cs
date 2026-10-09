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

/// <summary><c>Status (0x00) = [1, chain_id, genesis_hash, profiles, kinds, max_object_bytes]</c>.</summary>
public sealed class LeanStatusMessage(UInt256 chainId, ValueHash256 genesisHash, ValueHash256[] profiles, byte kinds, ulong maxObjectBytes) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Status;
    public UInt256 ChainId { get; } = chainId;
    public ValueHash256 GenesisHash { get; } = genesisHash;
    public ValueHash256[] Profiles { get; } = profiles;
    public byte Kinds { get; } = kinds;
    public ulong MaxObjectBytes { get; } = maxObjectBytes;

    public bool SupportsKind(byte kind) => (Kinds & (1 << (kind - 1))) != 0;
}

/// <summary><c>AnnounceObjects (0x01) = [descriptor, ...]</c>, in strictly ascending object ID order.</summary>
public sealed class AnnounceObjectsMessage(LeanDescriptor[] descriptors) : LeanMessage
{
    public override int PacketType => LeanMessageCode.AnnounceObjects;
    public LeanDescriptor[] Descriptors { get; } = descriptors;
}

/// <summary><c>GetObjects (0x02) = [request_id, selectors]</c>.</summary>
public sealed class GetObjectsMessage(ulong requestId, LeanSelector[] selectors) : LeanMessage
{
    public override int PacketType => LeanMessageCode.GetObjects;
    public ulong RequestId { get; } = requestId;
    public LeanSelector[] Selectors { get; } = selectors;
}

/// <summary>One <c>result = [status, descriptor_or_empty, auxiliary]</c> of an Objects response.</summary>
public readonly record struct LeanObjectResult(LeanResultStatus Status, LeanDescriptor? Descriptor, LeanHeaderSkeleton? Skeleton)
{
    public static LeanObjectResult Of(LeanResultStatus status) => new(status, null, null);
}

/// <summary><c>Objects (0x03) = [request_id, results]</c>.</summary>
public sealed class ObjectsMessage(ulong requestId, LeanObjectResult[] results) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Objects;
    public ulong RequestId { get; } = requestId;
    public LeanObjectResult[] Results { get; } = results;
}

/// <summary><c>GetChunks (0x04) = [request_id, object_id, indices]</c>.</summary>
public sealed class GetChunksMessage(ulong requestId, ValueHash256 objectId, int[] indices) : LeanMessage
{
    public override int PacketType => LeanMessageCode.GetChunks;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public int[] Indices { get; } = indices;
}

/// <summary><c>Chunk (0x05) = [request_id, object_id, index, chunk_bytes, branch]</c>.</summary>
public sealed class ChunkMessage(ulong requestId, ValueHash256 objectId, int index, ReadOnlyMemory<byte> data, ValueHash256[] branch) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Chunk;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public int Index { get; } = index;
    public ReadOnlyMemory<byte> Data { get; } = data;
    public ValueHash256[] Branch { get; } = branch;
}

/// <summary><c>Complete (0x06) = [request_id, object_id, status]</c>.</summary>
public sealed class CompleteMessage(ulong requestId, ValueHash256 objectId, LeanCompleteStatus status) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Complete;
    public ulong RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public LeanCompleteStatus Status { get; } = status;
}

/// <summary><c>Cancel (0x07) = [request_id]</c>.</summary>
public sealed class CancelMessage(ulong requestId) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Cancel;
    public ulong RequestId { get; } = requestId;
}

/// <summary><c>GetTransactions (0x08) = [request_id, transaction_hashes]</c>.</summary>
public sealed class GetTransactionsMessage(ulong requestId, ValueHash256[] hashes) : LeanMessage
{
    public override int PacketType => LeanMessageCode.GetTransactions;
    public ulong RequestId { get; } = requestId;
    public ValueHash256[] Hashes { get; } = hashes;
}

/// <summary><c>Transactions (0x09) = [request_id, results]</c> with <c>result = [status, transaction_envelope_or_empty]</c>.</summary>
public sealed class TransactionsMessage(ulong requestId, (LeanResultStatus Status, byte[] Envelope)[] results) : LeanMessage
{
    public override int PacketType => LeanMessageCode.Transactions;
    public ulong RequestId { get; } = requestId;
    public (LeanResultStatus Status, byte[] Envelope)[] Results { get; } = results;
}

/// <summary>Shared strict framing: a size bound before any parsing, one outer list, and no trailing bytes.</summary>
public abstract class LeanMessageSerializer<T> : IZeroMessageSerializer<T> where T : LeanMessage
{
    protected virtual int MaxBytes => LeanProtocol.MaxMessageBytes;

    public virtual void Serialize(IByteBuffer byteBuffer, T message)
    {
        byte[] encoded = Encode(message);
        if (encoded.Length > MaxBytes) throw new ArgumentException($"{typeof(T).Name} exceeds its message size limit");
        byteBuffer.WriteBytes(encoded);
    }

    public T Deserialize(IByteBuffer byteBuffer)
    {
        int length = byteBuffer.ReadableBytes;
        if (length > MaxBytes) throw new RlpException($"{typeof(T).Name} exceeds its message size limit");
        T message = Decode(LeanWire.Span(byteBuffer));
        byteBuffer.SkipBytes(length);
        return message;
    }

    /// <summary>Decodes message data, which must be exactly one canonical list.</summary>
    public T Decode(ReadOnlySpan<byte> data)
    {
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
            LeanRlp.EncodeBytes(message.GenesisHash.Bytes), LeanRlp.EncodeList(profiles), LeanRlp.EncodeUInt(message.Kinds),
            LeanRlp.EncodeUInt(message.MaxObjectBytes));
    }

    protected override LeanStatusMessage Decode(ref LeanRlpReader fields)
    {
        if (fields.ReadUInt64() != LeanProtocol.StatusVersion) throw new RlpException("Unknown lean Status version");
        UInt256 chainId = fields.ReadUInt256();
        ValueHash256 genesis = fields.ReadHash();
        ValueHash256[] profiles = ReadAscendingHashes(ref fields, LeanProtocol.MaxProfiles);
        ulong kinds = fields.ReadUInt64();
        // Bit zero (kind 1) is mandatory and only kinds 1 to 3 exist.
        if ((kinds & 1) == 0 || kinds > 0b111) throw new RlpException("Invalid lean kinds mask");
        ulong maxObjectBytes = fields.ReadUInt64();
        if (maxObjectBytes is 0 or > LeanProtocol.MaxObjectBytes) throw new RlpException("Invalid max_object_bytes");
        return new(chainId, genesis, profiles, (byte)kinds, maxObjectBytes);
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
            if (i > 0 && descriptors[i - 1].ObjectId.Bytes.SequenceCompareTo(descriptors[i].ObjectId.Bytes) >= 0)
                throw new RlpException("Announcements are not in strictly ascending object ID order");
        }
        return new(descriptors);
    }
}

public sealed class GetObjectsMessageSerializer : LeanMessageSerializer<GetObjectsMessage>
{
    public override byte[] Encode(GetObjectsMessage message)
    {
        byte[][] selectors = new byte[message.Selectors.Length][];
        for (int i = 0; i < selectors.Length; i++)
        {
            LeanSelector selector = message.Selectors[i];
            selectors[i] = LeanRlp.EncodeList(LeanRlp.EncodeUInt(selector.Kind), LeanRlp.EncodeBytes(selector.ProfileId.Bytes),
                LeanRlp.EncodeUInt(selector.LookupKind), LeanRlp.EncodeBytes(selector.LookupKey.Bytes));
        }
        return LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), LeanRlp.EncodeList(selectors));
    }

    protected override GetObjectsMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        LeanRlpReader list = fields.ReadList();
        int count = list.CountRemaining(LeanProtocol.MaxLookups);
        if (count == 0) throw new RlpException("Empty selector list");
        LeanSelector[] selectors = new LeanSelector[count];
        for (int i = 0; i < count; i++)
        {
            LeanRlpReader item = list.ReadList();
            byte kind = item.ReadUInt8();
            ValueHash256 profile = item.ReadHash();
            byte lookupKind = item.ReadUInt8();
            ValueHash256 key = item.ReadHash();
            item.End();
            if (kind is < LeanProtocol.KindWrapper or > LeanProtocol.KindInclusionList) throw new RlpException("Unknown selector kind");
            if (lookupKind > LeanProtocol.LookupTransaction || (lookupKind == LeanProtocol.LookupTransaction && kind != LeanProtocol.KindWrapper))
                throw new RlpException("Invalid lookup kind");
            selectors[i] = new(kind, profile, lookupKind, key);
            if (i > 0 && selectors[i - 1].CompareTo(selectors[i]) >= 0) throw new RlpException("Selectors are not strictly ascending");
        }
        list.End();
        return new(requestId, selectors);
    }
}

public sealed class ObjectsMessageSerializer : LeanMessageSerializer<ObjectsMessage>
{
    protected override int MaxBytes => LeanProtocol.MaxMetadataResponseBytes;

    public override byte[] Encode(ObjectsMessage message)
    {
        byte[][] results = new byte[message.Results.Length][];
        for (int i = 0; i < results.Length; i++) results[i] = EncodeResult(message.Results[i]);
        return LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), LeanRlp.EncodeList(results));
    }

    public static byte[] EncodeResult(LeanObjectResult result) => result.Status == LeanResultStatus.Ok
        ? LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), result.Descriptor!.Encoded, result.Skeleton?.Encoded ?? LeanRlp.EncodeBytes([]))
        : LeanRlp.EncodeList(LeanRlp.EncodeUInt((ulong)result.Status), LeanRlp.EncodeBytes([]), LeanRlp.EncodeBytes([]));

    protected override ObjectsMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        LeanRlpReader list = fields.ReadList();
        int count = list.CountRemaining(LeanProtocol.MaxLookups);
        if (count == 0) throw new RlpException("Empty Objects response");
        LeanObjectResult[] results = new LeanObjectResult[count];
        for (int i = 0; i < count; i++)
        {
            LeanRlpReader item = list.ReadList();
            byte status = item.ReadUInt8();
            if (status > (byte)LeanResultStatus.TooLarge) throw new RlpException("Unknown result status");
            if (status != (byte)LeanResultStatus.Ok)
            {
                item.ReadEmptyBytes();
                item.ReadEmptyBytes();
                results[i] = LeanObjectResult.Of((LeanResultStatus)status);
            }
            else
            {
                LeanDescriptor descriptor = LeanDescriptor.Decode(item.ReadItem());
                LeanHeaderSkeleton? skeleton = null;
                if (descriptor.Kind == LeanProtocol.KindBlockProof) skeleton = LeanHeaderSkeleton.Decode(item.ReadItem());
                else item.ReadEmptyBytes();
                results[i] = new(LeanResultStatus.Ok, descriptor, skeleton);
            }
            item.End();
        }
        list.End();
        return new(requestId, results);
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
    public const int MaxBranch = 10;

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
        int branchContent = message.Branch.Length * 33;
        int content = LeanRlp.LengthOfUInt(message.RequestId) + 33 + LeanRlp.LengthOfUInt((ulong)message.Index)
            + LeanRlp.LengthOfBytes(message.Data.Span) + LeanRlp.LengthOfList(branchContent);
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
        written = LeanRlp.WriteListPrefix(header, branchContent);
        buffer.WriteBytes(header[..written]);
        Span<byte> hash = stackalloc byte[33];
        hash[0] = 0xa0;
        foreach (ValueHash256 sibling in message.Branch)
        {
            sibling.Bytes.CopyTo(hash[1..]);
            buffer.WriteBytes(hash);
        }
    }

    protected override ChunkMessage Decode(ref LeanRlpReader fields)
    {
        LeanChunkView view = LeanChunkView.Read(ref fields);
        return new(view.RequestId, view.ObjectId, view.Index, view.Data.ToArray(), view.Branch);
    }
}

/// <summary>A received Chunk whose bytes still alias the packet, so they are verified and charged before any copy.</summary>
public readonly ref struct LeanChunkView
{
    public ulong RequestId { get; private init; }
    public ValueHash256 ObjectId { get; private init; }
    public int Index { get; private init; }
    public ReadOnlySpan<byte> Data { get; private init; }
    public ValueHash256[] Branch { get; private init; }

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
        LeanRlpReader branchList = fields.ReadList();
        int depth = branchList.CountRemaining(ChunkMessageSerializer.MaxBranch);
        ValueHash256[] branch = new ValueHash256[depth];
        for (int i = 0; i < depth; i++) branch[i] = branchList.ReadHash();
        branchList.End();
        return new LeanChunkView { RequestId = requestId, ObjectId = objectId, Index = (int)index, Data = data, Branch = branch };
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

public sealed class GetTransactionsMessageSerializer : LeanMessageSerializer<GetTransactionsMessage>
{
    public override byte[] Encode(GetTransactionsMessage message) =>
        LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), EncodeHashes(message.Hashes));

    protected override GetTransactionsMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        return new(requestId, ReadAscendingHashes(ref fields, LeanProtocol.MaxTxsPerRequest));
    }
}

public sealed class TransactionsMessageSerializer : LeanMessageSerializer<TransactionsMessage>
{
    protected override int MaxBytes => LeanProtocol.MaxTxResponseBytes;

    public override byte[] Encode(TransactionsMessage message)
    {
        byte[][] results = new byte[message.Results.Length][];
        for (int i = 0; i < results.Length; i++)
            results[i] = EncodeResult(message.Results[i].Status, message.Results[i].Envelope);
        return LeanRlp.EncodeList(LeanRlp.EncodeUInt(message.RequestId), LeanRlp.EncodeList(results));
    }

    public static byte[] EncodeResult(LeanResultStatus status, byte[] envelope) =>
        LeanRlp.EncodeList(LeanRlp.EncodeUInt((ulong)status), LeanRlp.EncodeBytes(status == LeanResultStatus.Ok ? envelope : []));

    protected override TransactionsMessage Decode(ref LeanRlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        LeanRlpReader list = fields.ReadList();
        int count = list.CountRemaining(LeanProtocol.MaxTxsPerRequest);
        if (count == 0) throw new RlpException("Empty Transactions response");
        (LeanResultStatus, byte[])[] results = new (LeanResultStatus, byte[])[count];
        for (int i = 0; i < count; i++)
        {
            LeanRlpReader item = list.ReadList();
            byte status = item.ReadUInt8();
            if (status > (byte)LeanResultStatus.TooLarge) throw new RlpException("Unknown result status");
            ReadOnlySpan<byte> envelope = item.ReadBytes();
            item.End();
            if ((status == (byte)LeanResultStatus.Ok) == (envelope.Length == 0)) throw new RlpException("Result status disagrees with its envelope");
            results[i] = ((LeanResultStatus)status, envelope.ToArray());
        }
        list.End();
        return new(requestId, results);
    }
}
