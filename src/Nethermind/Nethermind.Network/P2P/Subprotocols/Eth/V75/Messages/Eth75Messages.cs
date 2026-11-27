// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;

/// <summary>An EIP-8437 wrapper message.</summary>
public abstract class Eth75Message : P2PMessage
{
    public override string Protocol => "eth";
}

/// <summary><c>NewProofWrapperHashes (0x18) = [[mode, length, hash, txs-meta], ...]</c>: 1 to 64 held wrappers in strictly ascending
/// hash order.</summary>
/// <remarks>Entries of unsupported modes are checked for encoding, count, length and ordering, then left out of
/// <see cref="Announcements"/>, which may therefore be empty.</remarks>
public sealed class NewProofWrapperHashesMessage(WrapperAnnouncement[] announcements) : Eth75Message
{
    public override int PacketType => Eth75MessageCode.NewProofWrapperHashes;
    public WrapperAnnouncement[] Announcements { get; } = announcements;
}

/// <summary><c>GetProofWrappers (0x19) = [request-id, [hash, ...]]</c>: 1 to 32 distinct hashes.</summary>
/// <param name="compact">Whether the responder may replace transaction encodings it previously sent in full on the connection
/// with hash references; <c>eth/75</c> has no such field and always requires complete encodings.</param>
public sealed class GetProofWrappersMessage(long requestId, bool compact, ValueHash256[] hashes) : Eth75Message
{
    public override int PacketType => Eth75MessageCode.GetProofWrappers;
    public long RequestId { get; } = requestId;
    public bool Compact { get; } = compact;
    public ValueHash256[] Hashes { get; } = hashes;
}

/// <summary>A ProofWrappers entry <c>[hash, wrapper]</c>: the canonical wrapper hash and the wrapper as sent, embedded as its
/// own RLP item.</summary>
public readonly record struct ProofWrapperEntry(ValueHash256 Hash, byte[] Wrapper);

/// <summary><c>ProofWrappers (0x1a) = [request-id, [[hash, wrapper], ...]]</c>.</summary>
public sealed class ProofWrappersMessage(long requestId, ProofWrapperEntry[] entries) : Eth75Message
{
    public override int PacketType => Eth75MessageCode.ProofWrappers;
    public long RequestId { get; } = requestId;
    public ProofWrapperEntry[] Entries { get; } = entries;
}

/// <summary>Strict framing shared by the wrapper messages: a size bound before parsing, one outer list, no trailing bytes.</summary>
public abstract class Eth75MessageSerializer<T>(int maxBytes) : IZeroMessageSerializer<T> where T : Eth75Message
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
        Eth75RlpReader outer = new(data);
        Eth75RlpReader fields = outer.ReadList();
        outer.End();
        T message = Decode(ref fields);
        fields.End();
        return message;
    }

    public abstract byte[] Encode(T message);

    protected abstract T Decode(ref Eth75RlpReader fields);

    /// <summary>Reads an EIP-2481 request ID, any 64-bit value, held as its two's-complement <see cref="long"/> so it is echoed
    /// back unchanged.</summary>
    /// <remarks>This client issues IDs from 1 upward, so a response under an ID above 2^63 - 1 matches no request.</remarks>
    protected static long ReadRequestId(ref Eth75RlpReader fields) => unchecked((long)fields.ReadUInt64());
}

public sealed class NewProofWrapperHashesMessageSerializer() : Eth75MessageSerializer<NewProofWrapperHashesMessage>(Eth75Protocol.MaxNewProofWrapperHashesBytes)
{
    public override byte[] Encode(NewProofWrapperHashesMessage message)
    {
        byte[][] items = new byte[message.Announcements.Length][];
        for (int i = 0; i < items.Length; i++) items[i] = message.Announcements[i].Encoded;
        return Eth75Rlp.EncodeList(items);
    }

    protected override NewProofWrapperHashesMessage Decode(ref Eth75RlpReader fields)
    {
        int count = fields.CountRemaining(Eth75Protocol.MaxAnnouncements);
        if (count == 0) throw new RlpException("Empty announcement");
        List<WrapperAnnouncement> announcements = new(count);
        ValueHash256 previous = default;
        for (int i = 0; i < count; i++)
        {
            WrapperAnnouncement? announcement = WrapperAnnouncement.Decode(fields.ReadItem(), out ValueHash256 hash);
            if (i > 0 && previous.CompareTo(hash) >= 0) throw new RlpException("Announcements are not in strictly ascending hash order");
            previous = hash;
            if (announcement is not null) announcements.Add(announcement);
        }
        return new([.. announcements]);
    }
}

public sealed class GetProofWrappersMessageSerializer() : Eth75MessageSerializer<GetProofWrappersMessage>(Eth75Protocol.MaxGetProofWrappersBytes)
{
    public override byte[] Encode(GetProofWrappersMessage message)
    {
        if (message.Compact) throw new ArgumentException("eth/75 requests complete wrappers only", nameof(message));
        byte[][] ids = new byte[message.Hashes.Length][];
        for (int i = 0; i < ids.Length; i++) ids[i] = Eth75Rlp.EncodeBytes(message.Hashes[i].Bytes);
        return Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)message.RequestId), Eth75Rlp.EncodeList(ids));
    }

    protected override GetProofWrappersMessage Decode(ref Eth75RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth75RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth75Protocol.MaxWrappersPerRequest);
        if (count == 0) throw new RlpException("Empty wrapper request");
        ValueHash256[] ids = new ValueHash256[count];
        HashSet<ValueHash256> distinct = new(count);
        for (int i = 0; i < count; i++)
            if (!distinct.Add(ids[i] = list.ReadHash())) throw new RlpException("Requested hashes are not distinct");
        list.End();
        return new(requestId, false, ids);
    }
}

public sealed class ProofWrappersMessageSerializer() : Eth75MessageSerializer<ProofWrappersMessage>(Eth75Protocol.LocalMaxResponseBytes)
{
    // A wrapper is spliced in as its own RLP item: exactly the bytes its hash covers.
    public override byte[] Encode(ProofWrappersMessage message)
    {
        byte[][] entries = new byte[message.Entries.Length][];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(message.Entries[i].Hash.Bytes), message.Entries[i].Wrapper);
        return Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt((ulong)message.RequestId), Eth75Rlp.EncodeList(entries));
    }

    protected override ProofWrappersMessage Decode(ref Eth75RlpReader fields)
    {
        long requestId = ReadRequestId(ref fields);
        Eth75RlpReader list = fields.ReadList();
        int count = list.CountRemaining(Eth75Protocol.MaxWrappersPerRequest);
        ProofWrapperEntry[] entries = new ProofWrapperEntry[count];
        for (int i = 0; i < count; i++)
        {
            Eth75RlpReader entry = list.ReadList();
            ValueHash256 hash = entry.ReadHash();
            ReadOnlySpan<byte> wrapper = entry.ReadItem();
            entry.End();
            if (wrapper[0] < 0xc0) throw new RlpException("A wrapper is an RLP list");
            if (wrapper.Length > Eth75Protocol.MaxWrapperBytes) throw new RlpException("Wrapper exceeds MAX_WRAPPER_BYTES");
            entries[i] = new(hash, wrapper.ToArray());
        }
        list.End();
        return new(requestId, entries);
    }
}
