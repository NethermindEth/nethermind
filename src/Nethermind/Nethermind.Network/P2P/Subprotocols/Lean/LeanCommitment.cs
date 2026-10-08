// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Numerics;
using Nethermind.Core.Crypto;

namespace Nethermind.Network.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 domain-separated identities and the Merkle commitment over 64 KiB chunks.</summary>
public static class LeanCommitment
{
    public const int SeedLength = 1 + 32 + 32 + 32 + 8 + 4;

    public static ValueHash256 ProfileId(ReadOnlySpan<byte> aggregatedVk) => Domain("lean/1/profile\0"u8, aggregatedVk);

    public static ValueHash256 ContextHash(ReadOnlySpan<byte> encodedContext) => Domain("lean/1/context\0"u8, encodedContext);

    public static ValueHash256 ObjectId(ReadOnlySpan<byte> encodedDescriptor) => Domain("lean/1/object\0"u8, encodedDescriptor);

    public static ValueHash256 SkeletonHash(ReadOnlySpan<byte> encodedSkeleton) => Domain("lean/1/skeleton\0"u8, encodedSkeleton);

    public static int ChunkCount(ulong byteLength) => (int)((byteLength + LeanProtocol.ChunkBytes - 1) / LeanProtocol.ChunkBytes);

    public static int Depth(int chunkCount) => BitOperations.Log2(BitOperations.RoundUpToPowerOf2((uint)chunkCount));

    public static int ChunkLength(ulong byteLength, int index) =>
        (int)Math.Min(LeanProtocol.ChunkBytes, byteLength - (ulong)index * LeanProtocol.ChunkBytes);

    /// <summary><c>S = U8(kind) || profile_id || context_hash || content_hash || U64(byte_length) || U32(N)</c>.</summary>
    public static byte[] Seed(byte kind, in ValueHash256 profileId, in ValueHash256 contextHash, in ValueHash256 contentHash, ulong byteLength)
    {
        byte[] seed = new byte[SeedLength];
        seed[0] = kind;
        profileId.Bytes.CopyTo(seed.AsSpan(1));
        contextHash.Bytes.CopyTo(seed.AsSpan(33));
        contentHash.Bytes.CopyTo(seed.AsSpan(65));
        BinaryPrimitives.WriteUInt64BigEndian(seed.AsSpan(97), byteLength);
        BinaryPrimitives.WriteUInt32BigEndian(seed.AsSpan(105), (uint)ChunkCount(byteLength));
        return seed;
    }

    public static ValueHash256 Leaf(ReadOnlySpan<byte> seed, int index, ReadOnlySpan<byte> chunk)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update("lean/1/leaf\0"u8);
        hash.Update(seed);
        Span<byte> fields = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(fields, (uint)index);
        BinaryPrimitives.WriteUInt32BigEndian(fields[4..], (uint)chunk.Length);
        hash.Update(fields);
        hash.Update(chunk);
        return hash.GenerateValueHash();
    }

    public static ValueHash256 EmptyLeaf(ReadOnlySpan<byte> seed, int index)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update("lean/1/empty\0"u8);
        hash.Update(seed);
        Span<byte> field = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(field, (uint)index);
        hash.Update(field);
        return hash.GenerateValueHash();
    }

    public static ValueHash256 Node(int level, in ValueHash256 left, in ValueHash256 right)
    {
        Span<byte> preimage = stackalloc byte[12 + 1 + 64];
        "lean/1/node\0"u8.CopyTo(preimage);
        preimage[12] = (byte)level;
        left.Bytes.CopyTo(preimage[13..]);
        right.Bytes.CopyTo(preimage[45..]);
        return ValueKeccak.Compute(preimage);
    }

    public static ValueHash256 Root(ReadOnlySpan<byte> seed, in ValueHash256 treeRoot)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update("lean/1/root\0"u8);
        hash.Update(seed);
        hash.Update(treeRoot.Bytes);
        return hash.GenerateValueHash();
    }

    /// <summary>Canonical hash of the subtree at <paramref name="level"/> and <paramref name="position"/> holding only padding leaves.</summary>
    public static ValueHash256 PaddingSubtree(ReadOnlySpan<byte> seed, int level, int position)
    {
        int width = 1 << level;
        ValueHash256[] nodes = new ValueHash256[width];
        for (int i = 0; i < width; i++) nodes[i] = EmptyLeaf(seed, position * width + i);
        for (int l = 0; l < level; l++)
        {
            int count = width >> (l + 1);
            for (int i = 0; i < count; i++) nodes[i] = Node(l, nodes[2 * i], nodes[2 * i + 1]);
        }
        return nodes[0];
    }

    private static ValueHash256 Domain(ReadOnlySpan<byte> label, ReadOnlySpan<byte> value)
    {
        KeccakHash hash = KeccakHash.Create();
        hash.Update(label);
        hash.Update(value);
        return hash.GenerateValueHash();
    }
}

/// <summary>The full canonical tree over a body, used to serve branches and to recompute a reassembled root.</summary>
public sealed class LeanChunkTree
{
    private readonly ValueHash256[][] _levels;

    public LeanChunkTree(ReadOnlySpan<byte> seed, ReadOnlySpan<byte> body)
    {
        ChunkCount = LeanCommitment.ChunkCount((ulong)body.Length);
        Depth = LeanCommitment.Depth(ChunkCount);
        int width = 1 << Depth;
        _levels = new ValueHash256[Depth + 1][];
        ValueHash256[] leaves = new ValueHash256[width];
        for (int i = 0; i < width; i++)
        {
            leaves[i] = i < ChunkCount
                ? LeanCommitment.Leaf(seed, i, body.Slice(i * LeanProtocol.ChunkBytes, LeanCommitment.ChunkLength((ulong)body.Length, i)))
                : LeanCommitment.EmptyLeaf(seed, i);
        }
        _levels[0] = leaves;
        for (int level = 0; level < Depth; level++)
        {
            ValueHash256[] children = _levels[level];
            ValueHash256[] parents = new ValueHash256[children.Length / 2];
            for (int i = 0; i < parents.Length; i++) parents[i] = LeanCommitment.Node(level, children[2 * i], children[2 * i + 1]);
            _levels[level + 1] = parents;
        }
        ChunkRoot = LeanCommitment.Root(seed, _levels[Depth][0]);
    }

    public int ChunkCount { get; }
    public int Depth { get; }
    public ValueHash256 ChunkRoot { get; }

    /// <summary>Sibling hashes of chunk <paramref name="index"/> from the leaf level upward.</summary>
    public ValueHash256[] GetBranch(int index)
    {
        ValueHash256[] branch = new ValueHash256[Depth];
        for (int level = 0; level < Depth; level++) branch[level] = _levels[level][(index >> level) ^ 1];
        return branch;
    }
}

/// <summary>Verifies chunk branches against an untrusted descriptor's <c>chunk_root</c>.</summary>
/// <remarks>A branch authenticates the chunk only to the descriptor; it says nothing about the object's validity.</remarks>
public sealed class LeanBranchVerifier
{
    private readonly byte[] _seed;
    private readonly ValueHash256 _chunkRoot;
    private readonly int _chunkCount;
    private readonly ValueHash256?[] _padding;

    public LeanBranchVerifier(LeanDescriptor descriptor)
    {
        _seed = descriptor.Seed;
        _chunkRoot = descriptor.ChunkRoot;
        _chunkCount = descriptor.ChunkCount;
        Depth = descriptor.Depth;
        _padding = new ValueHash256?[Depth];
        for (int level = 0; level < Depth; level++)
        {
            int last = (_chunkCount - 1) >> level;
            if ((last & 1) == 0 && ((last + 1) << level) < (1 << Depth))
                _padding[level] = LeanCommitment.PaddingSubtree(_seed, level, last + 1);
        }
    }

    public int Depth { get; }

    public bool Verify(int index, ReadOnlySpan<byte> chunk, ReadOnlySpan<ValueHash256> branch)
    {
        if ((uint)index >= (uint)_chunkCount || branch.Length != Depth) return false;
        ValueHash256 current = LeanCommitment.Leaf(_seed, index, chunk);
        int lastAtLevel = _chunkCount - 1;
        for (int level = 0; level < Depth; level++, lastAtLevel >>= 1)
        {
            int position = index >> level;
            ref readonly ValueHash256 sibling = ref branch[level];
            if (position == lastAtLevel && (position & 1) == 0 && _padding[level] is { } padding && padding != sibling) return false;
            current = (position & 1) == 0
                ? LeanCommitment.Node(level, current, sibling)
                : LeanCommitment.Node(level, sibling, current);
        }
        return LeanCommitment.Root(_seed, current) == _chunkRoot;
    }
}
