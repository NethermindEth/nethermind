// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 <c>descriptor = [profile_id, byte_length, content_hash]</c>, committing to the complete wrapper body.</summary>
public sealed class LeanDescriptor
{
    private LeanDescriptor(ValueHash256 profileId, ulong byteLength, ValueHash256 contentHash, byte[] encoded)
    {
        ProfileId = profileId;
        ByteLength = byteLength;
        ContentHash = contentHash;
        Encoded = encoded;
        ObjectId = LeanCommitment.ObjectId(encoded);
        ChunkCount = LeanCommitment.ChunkCount(byteLength);
    }

    public ValueHash256 ProfileId { get; }
    public ulong ByteLength { get; }
    public ValueHash256 ContentHash { get; }
    public byte[] Encoded { get; }
    public ValueHash256 ObjectId { get; }
    public int ChunkCount { get; }
    public int ChunkLength(int index) => LeanCommitment.ChunkLength(ByteLength, index);

    public static LeanDescriptor Create(in ValueHash256 profileId, ReadOnlySpan<byte> body) =>
        Decode(LeanRlp.EncodeList(LeanRlp.EncodeBytes(profileId.Bytes), LeanRlp.EncodeUInt((ulong)body.Length),
            LeanRlp.EncodeBytes(ValueKeccak.Compute(body).Bytes)));

    public static LeanDescriptor Decode(ReadOnlySpan<byte> encoded)
    {
        LeanRlpReader outer = new(encoded);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        ValueHash256 profile = fields.ReadHash();
        ulong length = fields.ReadUInt64();
        if (length is 0 or > LeanProtocol.MaxObjectBytes) throw new RlpException("Object length is out of bounds");
        ValueHash256 contentHash = fields.ReadHash();
        fields.End();
        return new(profile, length, contentHash, encoded.ToArray());
    }

    public override string ToString() => $"wrapper {ObjectId.ToShortString()} ({ByteLength} bytes, {ChunkCount} chunks)";
}
