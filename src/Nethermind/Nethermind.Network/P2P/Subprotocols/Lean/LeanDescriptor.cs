// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 <c>descriptor = [kind, profile_id, context, byte_length, content_hash, chunk_root]</c>.</summary>
/// <remarks>
/// A well-formed descriptor fixes the object's geometry and identity, but its <c>chunk_root</c> is untrusted: only full
/// reconstruction and kind validation establish anything about the object.
/// </remarks>
public sealed class LeanDescriptor
{
    private byte[]? _seed;

    private LeanDescriptor(byte kind, in ValueHash256 profileId, byte[] context, ulong byteLength, in ValueHash256 contentHash,
        in ValueHash256 chunkRoot, byte[] encoded)
    {
        Kind = kind;
        ProfileId = profileId;
        Context = context;
        ByteLength = byteLength;
        ContentHash = contentHash;
        ChunkRoot = chunkRoot;
        Encoded = encoded;
        ObjectId = LeanCommitment.ObjectId(encoded);
        ContextHash = LeanCommitment.ContextHash(context);
        ChunkCount = LeanCommitment.ChunkCount(byteLength);
        Depth = LeanCommitment.Depth(ChunkCount);
    }

    public byte Kind { get; }
    public ValueHash256 ProfileId { get; }

    /// <summary>The context's canonical RLP list encoding.</summary>
    public byte[] Context { get; }
    public ulong ByteLength { get; }
    public ValueHash256 ContentHash { get; }
    public ValueHash256 ChunkRoot { get; }
    public byte[] Encoded { get; }
    public ValueHash256 ObjectId { get; }
    public ValueHash256 ContextHash { get; }
    public int ChunkCount { get; }
    public int Depth { get; }

    public ValueHash256 BlockHash { get; private init; }
    public ulong BlockNumber { get; private init; }
    public ValueHash256 TransactionsRoot { get; private init; }
    public ValueHash256 BlockDepsHash { get; private init; }
    public ValueHash256 SkeletonHash { get; private init; }
    public ValueHash256 PackageHash { get; private init; }

    /// <summary>The <c>lookup_kind = 0</c> key: object ID, block hash or package hash.</summary>
    public ValueHash256 PrimaryKey => Kind switch
    {
        LeanProtocol.KindBlockProof => BlockHash,
        LeanProtocol.KindInclusionList => PackageHash,
        _ => ObjectId
    };

    public byte[] Seed => _seed ??= LeanCommitment.Seed(Kind, ProfileId, ContextHash, ContentHash, ByteLength);

    public int ChunkLength(int index) => LeanCommitment.ChunkLength(ByteLength, index);

    public static byte[] WrapperContext() => LeanRlp.EncodeList();

    public static byte[] InclusionListContext(in ValueHash256 packageHash) => LeanRlp.EncodeList(LeanRlp.EncodeBytes(packageHash.Bytes));

    public static byte[] BlockProofContext(in ValueHash256 blockHash, ulong blockNumber, in ValueHash256 transactionsRoot,
        in ValueHash256 blockDepsHash, in ValueHash256 skeletonHash) =>
        LeanRlp.EncodeList(LeanRlp.EncodeBytes(blockHash.Bytes), LeanRlp.EncodeUInt(blockNumber),
            LeanRlp.EncodeBytes(transactionsRoot.Bytes), LeanRlp.EncodeBytes(blockDepsHash.Bytes), LeanRlp.EncodeBytes(skeletonHash.Bytes));

    /// <summary>Builds the descriptor and canonical tree of a locally held body.</summary>
    public static LeanDescriptor Create(byte kind, in ValueHash256 profileId, byte[] context, ReadOnlySpan<byte> body, out LeanChunkTree tree)
    {
        if (body.Length == 0 || (ulong)body.Length > LeanProtocol.MaxObjectBytes) throw new ArgumentException("Object body length is out of bounds");
        ValueHash256 contentHash = ValueKeccak.Compute(body);
        ValueHash256 contextHash = LeanCommitment.ContextHash(context);
        byte[] seed = LeanCommitment.Seed(kind, profileId, contextHash, contentHash, (ulong)body.Length);
        tree = new LeanChunkTree(seed, body);
        byte[] encoded = LeanRlp.EncodeList(LeanRlp.EncodeUInt(kind), LeanRlp.EncodeBytes(profileId.Bytes), context,
            LeanRlp.EncodeUInt((ulong)body.Length), LeanRlp.EncodeBytes(contentHash.Bytes), LeanRlp.EncodeBytes(tree.ChunkRoot.Bytes));
        LeanDescriptor descriptor = Decode(encoded);
        descriptor._seed = seed;
        return descriptor;
    }

    /// <summary>Decodes and checks a descriptor's encoding, context form and geometry.</summary>
    /// <exception cref="RlpException">The descriptor is malformed.</exception>
    public static LeanDescriptor Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length > LeanProtocol.MaxDescriptorBytes) throw new RlpException("Descriptor exceeds 512 bytes");
        LeanRlpReader outer = new(encoded);
        LeanRlpReader reader = outer.ReadList();
        outer.End();
        byte kind = reader.ReadUInt8();
        if (kind is not (LeanProtocol.KindWrapper or LeanProtocol.KindBlockProof or LeanProtocol.KindInclusionList))
            throw new RlpException($"Unknown object kind {kind}");
        ValueHash256 profileId = reader.ReadHash();
        byte[] context = reader.ReadItem().ToArray();
        ulong byteLength = reader.ReadUInt64();
        if (byteLength is 0 or > LeanProtocol.MaxObjectBytes) throw new RlpException("Object length is out of bounds");
        ValueHash256 contentHash = reader.ReadHash();
        ValueHash256 chunkRoot = reader.ReadHash();
        reader.End();

        LeanRlpReader contextOuter = new(context);
        LeanRlpReader fields = contextOuter.ReadList();
        contextOuter.End();
        LeanDescriptor descriptor;
        switch (kind)
        {
            case LeanProtocol.KindWrapper:
                fields.End();
                descriptor = new(kind, profileId, context, byteLength, contentHash, chunkRoot, encoded.ToArray());
                break;
            case LeanProtocol.KindBlockProof:
                {
                    ValueHash256 blockHash = fields.ReadHash();
                    ulong blockNumber = fields.ReadUInt64();
                    ValueHash256 transactionsRoot = fields.ReadHash();
                    ValueHash256 blockDepsHash = fields.ReadHash();
                    ValueHash256 skeletonHash = fields.ReadHash();
                    fields.End();
                    descriptor = new(kind, profileId, context, byteLength, contentHash, chunkRoot, encoded.ToArray())
                    {
                        BlockHash = blockHash,
                        BlockNumber = blockNumber,
                        TransactionsRoot = transactionsRoot,
                        BlockDepsHash = blockDepsHash,
                        SkeletonHash = skeletonHash
                    };
                    break;
                }
            default:
                {
                    ValueHash256 packageHash = fields.ReadHash();
                    fields.End();
                    // package_hash = H(body) = content_hash, so any other value cannot describe a valid package.
                    if (packageHash != contentHash) throw new RlpException("Package hash differs from content hash");
                    descriptor = new(kind, profileId, context, byteLength, contentHash, chunkRoot, encoded.ToArray()) { PackageHash = packageHash };
                    break;
                }
        }
        return descriptor;
    }

    public override string ToString() => $"kind {Kind} object {ObjectId.ToShortString()} ({ByteLength} bytes, {ChunkCount} chunks)";
}

/// <summary>EIP-8437 <c>selector = [kind, profile_id, lookup_kind, lookup_key]</c>.</summary>
public readonly record struct LeanSelector(byte Kind, ValueHash256 ProfileId, byte LookupKind, ValueHash256 LookupKey) : IComparable<LeanSelector>
{
    public int CompareTo(LeanSelector other)
    {
        int result = Kind.CompareTo(other.Kind);
        if (result == 0) result = ProfileId.Bytes.SequenceCompareTo(other.ProfileId.Bytes);
        if (result == 0) result = LookupKind.CompareTo(other.LookupKind);
        if (result == 0) result = LookupKey.Bytes.SequenceCompareTo(other.LookupKey.Bytes);
        return result;
    }

    /// <summary>Whether a descriptor answers this selector's lookup.</summary>
    /// <remarks>A transaction-hash lookup can only be confirmed against the reconstructed body.</remarks>
    public bool Matches(LeanDescriptor descriptor) =>
        descriptor.Kind == Kind && descriptor.ProfileId == ProfileId
        && (LookupKind == LeanProtocol.LookupTransaction || descriptor.PrimaryKey == LookupKey);
}
