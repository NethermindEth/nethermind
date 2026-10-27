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

/// <summary><c>NewProofWrapperHashes (0x16) = [[mode, length, hash], ...]</c>: 1 to 64 held wrappers in strictly ascending hash order.</summary>
public sealed class NewProofWrapperHashesMessage(WrapperAnnouncement[] announcements) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.NewProofWrapperHashes;
    public WrapperAnnouncement[] Announcements { get; } = announcements;
}

/// <summary><c>GetProofWrappers (0x17) = [request-id, compact, [hash, ...]]</c>: 1 to 32 distinct hashes.</summary>
/// <param name="compact">Whether the responder may replace transaction encodings it previously sent in full on the connection
/// with hash references; <c>false</c> requires complete encodings.</param>
public sealed class GetProofWrappersMessage(long requestId, bool compact, ValueHash256[] hashes) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.GetProofWrappers;
    public long RequestId { get; } = requestId;
    public bool Compact { get; } = compact;
    public ValueHash256[] Hashes { get; } = hashes;
}

/// <summary>A ProofWrappers entry <c>[hash, wrapper]</c>: the canonical wrapper hash and the wrapper as sent, embedded as its
/// own RLP item.</summary>
public readonly record struct ProofWrapperEntry(ValueHash256 Hash, byte[] Wrapper);

/// <summary><c>ProofWrappers (0x18) = [request-id, [[hash, wrapper], ...]]</c>.</summary>
public sealed class ProofWrappersMessage(long requestId, ProofWrapperEntry[] entries) : Eth73Message
{
    public override int PacketType => Eth73MessageCode.ProofWrappers;
    public long RequestId { get; } = requestId;
    public ProofWrapperEntry[] Entries { get; } = entries;
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

    /// <summary>Reads an EIP-2481 request ID, any 64-bit value, held as its two's-complement <see cref="long"/> so it is echoed
    /// back unchanged.</summary>
    /// <remarks>This client issues IDs from 1 upward, so a response under an ID above 2^63 - 1 matches no request.</remarks>
    protected static long ReadRequestId(ref Eth73RlpReader fields) => unchecked((long)fields.ReadUInt64());
}

public sealed class NewProofWrapperHashesMessageSerializer() : Eth73MessageSerializer<NewProofWrapperHashesMessage>(Eth73Protocol.MaxNewProofWrapperHashesBytes)
{
    public override byte[] Encode(NewProofWrapperHashesMessage message)
    {
        byte[][] items = new byte[message.Announcements.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = message.Announcements[i].Encoded;
        return Eth73Rlp.EncodeList(items);
    }

    protected override NewProofWrapperHashesMessage Decode(ref Eth73RlpReader fields)
    {
        int count = fields.CountRemaining(Eth73Protocol.MaxAnnouncements);
        if (count == 0) throw new RlpException("Empty announcement");
        WrapperAnnouncement[] announcements = new WrapperAnnouncement[count];
        for (int i = 0; i < count; i++)
        {
            announcements[i] = WrapperAnnouncement.Decode(fields.ReadItem());
            if (i > 0 && announcements[i - 1].Hash.CompareTo(announcements[i].Hash) >= 0)
                throw new RlpException("Announcements are not in strictly ascending hash order");
        }
        return new(announcements);
    }
}

public sealed class GetProofWrappersMessageSerializer() : Eth73MessageSerializer<GetProofWrappersMessage>(Eth73Protocol.MaxGetProofWrappersBytes)
{
    public override byte[] Encode(GetProofWrappersMessage message)
    {
        byte[][] ids = new byte[message.Hashes.Length][];
        for (int i = 0; i < ids.Length; i++) ids[i] = Eth73Rlp.EncodeBytes(message.Hashes[i].Bytes);
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), Eth73Rlp.EncodeUInt(message.Compact ? 1UL : 0UL),
            Eth73Rlp.EncodeList(ids));
    }

    protected override GetProofWrappersMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        ulong compact = fields.ReadUInt64();
        if (compact > 1) throw new RlpException("The compact flag must be 0 or 1");
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxWrappersPerRequest);
        if (count == 0) throw new RlpException("Empty wrapper request");
        ValueHash256[] ids = new ValueHash256[count];
        HashSet<ValueHash256> distinct = new(count);
        for (int i = 0; i < count; i++)
            if (!distinct.Add(ids[i] = list.ReadHash())) throw new RlpException("Requested hashes are not distinct");
        list.End();
        return new(requestId, compact == 1, ids);
    }
}

public sealed class ProofWrappersMessageSerializer() : Eth73MessageSerializer<ProofWrappersMessage>(Eth73Protocol.LocalMaxResponseBytes)
{
    // A wrapper is spliced in as its own RLP item: exactly the bytes its hash covers.
    public override byte[] Encode(ProofWrappersMessage message)
    {
        byte[][] entries = new byte[message.Entries.Length][];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(message.Entries[i].Hash.Bytes), message.Entries[i].Wrapper);
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)message.RequestId), Eth73Rlp.EncodeList(entries));
    }

    protected override ProofWrappersMessage Decode(ref Eth73RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth73RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth73Protocol.MaxWrappersPerRequest);
        ProofWrapperEntry[] entries = new ProofWrapperEntry[count];
        for (int i = 0; i < count; i++)
        {
            Eth73RlpReader entry = list.ReadList();
            ValueHash256 hash = entry.ReadHash();
            ReadOnlySpan<byte> wrapper = entry.ReadItem();
            entry.End();
            if (wrapper[0] < 0xc0) throw new RlpException("A wrapper is an RLP list");
            if (wrapper.Length > Eth73Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
            entries[i] = new(hash, wrapper.ToArray());
        }
        list.End();
        return new(requestId, entries);
    }
}
