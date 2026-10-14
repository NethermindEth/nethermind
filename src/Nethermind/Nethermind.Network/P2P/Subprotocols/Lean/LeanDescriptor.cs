// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 descriptor, committing to the complete canonical body.</summary>
public sealed class LeanDescriptor
{
    private LeanDescriptor(byte kind, ValueHash256 profileId, byte[] context, ulong byteLength, ValueHash256 contentHash,
        byte[] encoded, ValueHash256 blockHash)
    {
        Kind = kind;
        ProfileId = profileId;
        Context = context;
        ByteLength = byteLength;
        ContentHash = contentHash;
        Encoded = encoded;
        BlockHash = blockHash;
        ObjectId = LeanCommitment.ObjectId(encoded);
        ChunkCount = LeanCommitment.ChunkCount(byteLength);
    }
    public byte Kind { get; }
    public ValueHash256 ProfileId { get; }
    public byte[] Context { get; }
    public ulong ByteLength { get; }
    public ValueHash256 ContentHash { get; }
    public byte[] Encoded { get; }
    public ValueHash256 ObjectId { get; }
    public int ChunkCount { get; }
    public ValueHash256 BlockHash { get; }
    public ValueHash256 PrimaryKey => Kind == LeanProtocol.KindBlockProof ? BlockHash : ObjectId;
    public int ChunkLength(int index) => LeanCommitment.ChunkLength(ByteLength, index);
    public static byte[] WrapperContext() => LeanRlp.EncodeList();
    public static byte[] BlockProofContext(in ValueHash256 blockHash) => LeanRlp.EncodeList(LeanRlp.EncodeBytes(blockHash.Bytes));
    public static LeanDescriptor Create(byte kind, in ValueHash256 profileId, byte[] context, ReadOnlySpan<byte> body) =>
        Decode(LeanRlp.EncodeList(LeanRlp.EncodeUInt(kind), LeanRlp.EncodeBytes(profileId.Bytes), context,
            LeanRlp.EncodeUInt((ulong)body.Length), LeanRlp.EncodeBytes(ValueKeccak.Compute(body).Bytes)));
    public static LeanDescriptor Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > LeanProtocol.MaxDescriptorBytes) throw new RlpException("Descriptor exceeds 256 bytes");
        LeanRlpReader outer = new(encoded);
        LeanRlpReader fields = outer.ReadList();
        outer.End();
        byte kind = fields.ReadUInt8();
        if (kind is not (LeanProtocol.KindWrapper or LeanProtocol.KindBlockProof)) throw new RlpException("Unknown object kind");
        ValueHash256 profile = fields.ReadHash();
        byte[] context = fields.ReadItem().ToArray();
        ulong length = fields.ReadUInt64();
        if (length is 0 or > LeanProtocol.MaxObjectBytes) throw new RlpException("Object length is out of bounds");
        ValueHash256 contentHash = fields.ReadHash();
        fields.End();
        LeanRlpReader contextOuter = new(context);
        LeanRlpReader contextFields = contextOuter.ReadList();
        contextOuter.End();
        ValueHash256 block = kind == LeanProtocol.KindBlockProof ? contextFields.ReadHash() : default;
        contextFields.End();
        return new(kind, profile, context, length, contentHash, encoded.ToArray(), block);
    }
    public override string ToString() => $"kind {Kind} object {ObjectId.ToShortString()} ({ByteLength} bytes, {ChunkCount} chunks)";
}

/// <summary>EIP-8437 selector = [kind, profile_id, lookup_key].</summary>
public readonly record struct LeanSelector(byte Kind, ValueHash256 ProfileId, ValueHash256 LookupKey) : IComparable<LeanSelector>
{
    public int CompareTo(LeanSelector other)
    {
        int result = Kind.CompareTo(other.Kind);
        if (result == 0) result = ProfileId.CompareTo(other.ProfileId);
        if (result == 0) result = LookupKey.CompareTo(other.LookupKey);
        return result;
    }
    public bool Matches(LeanDescriptor descriptor) =>
        descriptor.Kind == Kind && descriptor.ProfileId == ProfileId && descriptor.PrimaryKey == LookupKey;
}
