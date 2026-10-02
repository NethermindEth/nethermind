// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using DotNetty.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Messages;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>Identifies the prototype chain and pinned recursive guest before proof gossip.</summary>
public sealed class LeanStatusMessage(ulong chainId, Hash256 genesisHash, byte[] verificationKey) : P2PMessage
{
    public override string Protocol => LeanProtocolHandler.Code;
    public override int PacketType => 0;
    public ulong ChainId { get; } = chainId;
    public Hash256 GenesisHash { get; } = genesisHash;
    public byte[] VerificationKey { get; } = verificationKey;
}

/// <summary>One complete RLP proof wrapper on the negotiated lean protocol.</summary>
public sealed class LeanProofWrapperMessage(byte[] wrapper) : P2PMessage
{
    public override string Protocol => LeanProtocolHandler.Code;
    public override int PacketType => 1;
    public byte[] Wrapper { get; } = wrapper;
}

/// <summary>Fixed-width chain ID, genesis hash and guest key encoding.</summary>
public sealed class LeanStatusMessageSerializer : IZeroMessageSerializer<LeanStatusMessage>
{
    public void Serialize(IByteBuffer buffer, LeanStatusMessage message)
    {
        if (message.VerificationKey.Length != 32) throw new ArgumentException("Invalid lean guest key");
        byte[] bytes = new byte[72];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, message.ChainId);
        message.GenesisHash.Bytes.CopyTo(bytes.AsSpan(8));
        message.VerificationKey.CopyTo(bytes.AsSpan(40));
        buffer.WriteBytes(bytes);
    }

    public LeanStatusMessage Deserialize(IByteBuffer buffer)
    {
        if (buffer.ReadableBytes != 72) throw new RlpException("Invalid lean status length");
        byte[] bytes = new byte[72];
        buffer.ReadBytes(bytes);
        return new(BinaryPrimitives.ReadUInt64BigEndian(bytes), new Hash256(bytes.AsSpan(8, 32)), bytes.AsSpan(40, 32).ToArray());
    }
}

/// <summary>Transports an already framed proof wrapper, with a size bound before allocation.</summary>
public sealed class LeanProofWrapperMessageSerializer : IZeroMessageSerializer<LeanProofWrapperMessage>
{
    public void Serialize(IByteBuffer buffer, LeanProofWrapperMessage message)
    {
        if (message.Wrapper.Length is 0 or > LeanProofStore.MaxWrapperBytes) throw new ArgumentException("Invalid lean wrapper size");
        buffer.WriteBytes(message.Wrapper);
    }

    public LeanProofWrapperMessage Deserialize(IByteBuffer buffer)
    {
        if (buffer.ReadableBytes is 0 or > LeanProofStore.MaxWrapperBytes) throw new RlpException("Invalid lean wrapper size");
        byte[] bytes = new byte[buffer.ReadableBytes];
        buffer.ReadBytes(bytes);
        return new(bytes);
    }
}
