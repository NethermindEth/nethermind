// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;

/// <summary>An EIP-8437 wrapper message.</summary>
public abstract class Eth73Message : P2PMessage
{
    public override string Protocol => "eth";
}

/// <summary><c>AnnounceProofWrappers (0x16) = [descriptor, ...]</c>: 1 to 64 descriptors in strictly ascending object ID order.</summary>
public sealed class AnnounceProofWrappersMessage(WrapperDescriptor[] descriptors) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.AnnounceProofWrappers;
    public WrapperDescriptor[] Descriptors { get; } = descriptors;
}

/// <summary><c>GetProofWrappers (0x17) = [request_id, [object_id, ...]]</c>: 1 to 64 distinct object IDs.</summary>
public sealed class GetProofWrappersMessage(long requestId, ValueHash256[] objectIds) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.GetProofWrappers;
    public long RequestId { get; } = requestId;
    public ValueHash256[] ObjectIds { get; } = objectIds;
}

/// <summary><c>ProofWrappers (0x18) = [request_id, [wrapper, ...]]</c>, each wrapper embedded as its own RLP item.</summary>
public sealed class ProofWrappersMessage(long requestId, byte[][] wrappers) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.ProofWrappers;
    public long RequestId { get; } = requestId;
    public byte[][] Wrappers { get; } = wrappers;
}

/// <summary><c>GetWrapperEnvelopes (0x19) = [request_id, object_id, [index, ...]]</c>: 1 to 1024 strictly ascending indices.</summary>
public sealed class GetWrapperEnvelopesMessage(long requestId, ValueHash256 objectId, int[] indices) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.GetWrapperEnvelopes;
    public long RequestId { get; } = requestId;
    public ValueHash256 ObjectId { get; } = objectId;
    public int[] Indices { get; } = indices;
}

/// <summary><c>WrapperEnvelopes (0x1a) = [request_id, [envelope, ...]]</c>: a prefix of the requested envelopes, or none.</summary>
public sealed class WrapperEnvelopesMessage(long requestId, byte[][] envelopes) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.WrapperEnvelopes;
    public long RequestId { get; } = requestId;
    public byte[][] Envelopes { get; } = envelopes;
}

/// <summary>Strict framing shared by the wrapper messages: a size bound before parsing, one outer list, no trailing bytes.</summary>
public abstract class Eth73MessageSerializer<T>(int maxBytes) : IZeroMessageSerializer<T> where T : Eth73Message
{
    public void Serialize(IByteBuffer byteBuffer, T message)
    {
        byte[] encoded = Encode(message);
        if (encoded.Length > maxBytes) throw new ArgumentException($"{typeof(T).Name} exceeds its message size limit");
        byteBuffer.WriteBytes(encoded);
    }

    public T Deserialize(IByteBuffer byteBuffer)
    {
        int length = byteBuffer.ReadableBytes;
        if (length > maxBytes) throw new RlpException($"{typeof(T).Name} exceeds its message size limit");
        byte[] data = new byte[length];
        byteBuffer.ReadBytes(data);
        return Decode(data);
    }

    /// <summary>Decodes message data, which must be exactly one canonical list.</summary>
    public T Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length > maxBytes) throw new RlpException($"{typeof(T).Name} exceeds its message size limit");
        Eth73RlpReader outer = new(data);
        Eth73RlpReader fields = outer.ReadList();
        outer.End();
        T message = Decode(ref fields);
        fields.End();
        return message;
    }

    public abstract byte[] Encode(T message);

    protected abstract T Decode(ref Eth73RlpReader fields);

    protected static long ReadRequestId(ref Eth73RlpReader fields)
    {
        ulong requestId = fields.ReadUInt64();
        if (requestId > long.MaxValue) throw new RlpException("Request ID exceeds 63 bits");
        return (long)requestId;
    }

    protected static byte[] EncodeByteStrings(IReadOnlyList<byte[]> items)
    {
        byte[][] encoded = new byte[items.Count][];
        for (int i = 0; i < encoded.Length; i++) encoded[i] = Eth73Rlp.EncodeBytes(items[i]);
        return Eth73Rlp.EncodeList(encoded);
    }
}

public sealed class AnnounceProofWrappersMessageSerializer() : Eth73MessageSerializer<AnnounceProofWrappersMessage>(Eth73Protocol.MaxRequestBytes)
{
    public override byte[] Encode(AnnounceProofWrappersMessage message)
    {
        byte[][] items = new byte[message.Descriptors.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = message.Descriptors[i].Encoded;
        return Eth73Rlp.EncodeList(items);
    }

    protected override AnnounceProofWrappersMessage Decode(ref Eth73RlpReader fields)
    {
        int count = fields.CountRemaining(Eth73Protocol.MaxAnnouncements);
        if (count == 0) throw new RlpException("Empty announcement");
        WrapperDescriptor[] descriptors = new WrapperDescriptor[count];
        for (int i = 0; i < count; i++)
        {
            descriptors[i] = WrapperDescriptor.Decode(fields.ReadItem());
            if (i > 0 && descriptors[i - 1].ObjectId.CompareTo(descriptors[i].ObjectId) >= 0)
                throw new RlpException("Announcements are not in strictly ascending object ID order");
        }
        return new(descriptors);
    }
}

public sealed class GetProofWrappersMessageSerializer() : Eth73MessageSerializer<GetProofWrappersMessage>(Eth73Protocol.MaxRequestBytes)
{
    public override byte[] Encode(GetProofWrappersMessage message)
    {
        byte[][] ids = new byte[message.ObjectIds.Length][];
        for (int i = 0; i < ids.Length; i++) ids[i] = Eth73Rlp.EncodeBytes(message.ObjectIds[i].Bytes);
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), Eth73Rlp.EncodeList(ids));
    }

    protected override GetProofWrappersMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxWrappersPerRequest);
        if (count == 0) throw new RlpException("Empty wrapper request");
        ValueHash256[] ids = new ValueHash256[count];
        HashSet<ValueHash256> distinct = new(count);
        for (int i = 0; i < count; i++)
            if (!distinct.Add(ids[i] = list.ReadHash())) throw new RlpException("Requested object IDs are not distinct");
        list.End();
        return new(requestId, ids);
    }
}

public sealed class ProofWrappersMessageSerializer() : Eth73MessageSerializer<ProofWrappersMessage>(Eth73Protocol.LocalMaxResponseBytes)
{
    // A wrapper is spliced in as its own RLP item: exactly the bytes its content_hash covers.
    public override byte[] Encode(ProofWrappersMessage message) =>
        Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), Eth73Rlp.EncodeList(message.Wrappers));

    protected override ProofWrappersMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxWrappersPerRequest);
        byte[][] wrappers = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> wrapper = list.ReadItem();
            if (wrapper[0] < 0xc0) throw new RlpException("A wrapper is an RLP list");
            if (wrapper.Length > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
            wrappers[i] = wrapper.ToArray();
        }
        list.End();
        return new(requestId, wrappers);
    }
}

public sealed class GetWrapperEnvelopesMessageSerializer() : Eth73MessageSerializer<GetWrapperEnvelopesMessage>(Eth73Protocol.MaxRequestBytes)
{
    public override byte[] Encode(GetWrapperEnvelopesMessage message)
    {
        byte[][] indices = new byte[message.Indices.Length][];
        for (int i = 0; i < indices.Length; i++) indices[i] = Eth73Rlp.EncodeUInt((ulong)message.Indices[i]);
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), Eth73Rlp.EncodeBytes(message.ObjectId.Bytes),
            Eth73Rlp.EncodeList(indices));
    }

    protected override GetWrapperEnvelopesMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        ValueHash256 objectId = fields.ReadHash();
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxEnvelopesPerRequest);
        if (count == 0) throw new RlpException("Empty envelope request");
        int[] indices = new int[count];
        for (int i = 0; i < count; i++)
        {
            ulong index = list.ReadUInt64();
            // Every wrapper lists fewer than MAX_TXS_PER_WRAPPER transactions, whether or not the responder holds it.
            if (index >= Core.Eip8288Constants.MaxTxsPerWrapper) throw new RlpException("Envelope index exceeds MAX_TXS_PER_WRAPPER");
            indices[i] = (int)index;
            if (i > 0 && indices[i - 1] >= indices[i]) throw new RlpException("Envelope indices are not strictly ascending");
        }
        list.End();
        return new(requestId, objectId, indices);
    }
}

public sealed class WrapperEnvelopesMessageSerializer() : Eth73MessageSerializer<WrapperEnvelopesMessage>(Eth73Protocol.LocalMaxResponseBytes)
{
    public override byte[] Encode(WrapperEnvelopesMessage message) =>
        Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), EncodeByteStrings(message.Envelopes));

    protected override WrapperEnvelopesMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxEnvelopesPerRequest);
        byte[][] envelopes = new byte[count][];
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> envelope = list.ReadBytes();
            if (envelope.IsEmpty) throw new RlpException("Empty transaction envelope");
            envelopes[i] = envelope.ToArray();
        }
        list.End();
        return new(requestId, envelopes);
    }
}
