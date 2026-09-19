// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal static class NodeViews
{
    /// <summary>Diagnostic count of child branches hashed by SIMD on the current thread.</summary>
    /// <remarks>Read deltas on the initializing thread; scalar hashes and padding lanes are excluded. Production behavior must not depend on it.</remarks>
    [ThreadStatic]
    internal static long BatchedBranchHashes;

    private const int MaxNibbles = 2 * Hash256.Size;
    private const int Avx2HashBatchSize = 4;
    private const int Avx512HashBatchSize = 8;
    private const int VectorByteLength = 32;
    private const int BranchHashBufferLength = Avx512HashBatchSize * (KeccakHash.Hash532InputLength + Hash256.Size);
    private static int MinHashBatchSize => Avx512F.IsSupported ? 3 : Avx2HashBatchSize;

    public static NodeView FromRoot(TrieNode? root, int depth, ITrieNodeResolver resolver)
    {
        if (root is null) return NodeView.Empty;

        ReadOnlySpan<byte> rootRlp = root.FullRlp.AsSpan();
        if (depth == 0) return root.IsBranch ? AsBranch(rootRlp, root.Keccak) : NodeView.Whole(rootRlp, root.Keccak?.ValueHash256);
        if (root.IsBranch) throw new InvalidOperationException("A partial trie holding one prefix cannot have a branch at its root.");

        byte[] key = root.Key!;
        if (key.Length < depth) throw new InvalidOperationException("A partial trie's root does not cover the prefix it was built for.");

        ReadOnlySpan<byte> rest = key.AsSpan(depth);
        if (root.IsLeaf) return NodeView.Leaf(rest, root.Value.AsSpan());

        TreePath path = TreePath.Empty;
        TrieNode child = root.GetChild(resolver, ref path, 0) ?? throw new InvalidOperationException("An extension node lost its child between commit and view.");
        ReadOnlySpan<byte> childRlp = child.FullRlp.AsSpan();
        if (rest.IsEmpty) return child.IsBranch ? AsBranch(childRlp, child.Keccak) : NodeView.Whole(childRlp, child.Keccak?.ValueHash256);

        Span<byte> reference = stackalloc byte[Hash256.Size];
        int referenceLength = BranchRlp.ReferenceOf(childRlp, reference);
        return NodeView.Extension(rest, reference[..referenceLength]);
    }

    public static NodeView FromRlp(ReadOnlySpan<byte> rlp, ValueHash256? knownHash = null)
    {
        ChildVector children = ChildVector.Rent();
        bool isBranch;
        try
        {
            isBranch = BranchRlp.TryReadChildren(rlp, children);
        }
        catch
        {
            ChildVector.Return(children);
            throw;
        }

        if (isBranch) return NodeView.Branch(children, rlp, knownHash);

        ChildVector.Return(children);
        return NodeView.Whole(rlp, knownHash);
    }

    /// <summary>Initializes one view for each of a branch's child RLPs.</summary>
    /// <remarks>The destination must be zero-initialized. The caller must release initialized views even if decoding throws.</remarks>
    public static void FromChildrenRlp(ReadOnlySpan<byte[]?> children, Span<NodeView> views)
    {
        uint initialized = 0;
        if (Avx2.IsSupported)
        {
            uint candidates = 0;

            for (int index = 0; index < BranchRlp.ChildCount; index++)
            {
                if (children[index]?.Length == KeccakHash.Hash532InputLength) candidates |= 1u << index;
            }
            if (BitOperations.PopCount(candidates) >= MinHashBatchSize)
                initialized = candidates ^ InitializeBatched(children, views, candidates);
        }

        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if ((initialized & (1u << index)) != 0) continue;
            byte[]? child = children[index];
            views[index] = child is null ? NodeView.Empty : FromRlp(child);
        }
    }

    [InlineArray(BranchHashBufferLength / VectorByteLength)]
    private struct BranchHashBuffer
    {
        private Vector256<ulong> _element0;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static uint InitializeBatched(ReadOnlySpan<byte[]?> children, Span<NodeView> views, uint candidates)
    {
        int batchSize = Avx512F.IsSupported ? Avx512HashBatchSize : Avx2HashBatchSize;
        int inputLength = Avx512F.IsSupported ? KeccakHash.Hash532InputLength : KeccakHash.Hash532PaddedLength;
        Unsafe.SkipInit(out BranchHashBuffer buffer);
        Span<byte> storage = MemoryMarshal.AsBytes((Span<Vector256<ulong>>)buffer);
        Span<byte> inputs = storage[..(batchSize * inputLength)];
        Span<byte> hashes = storage[(batchSize * inputLength)..];
        do
        {
            int count = Math.Min(batchSize, BitOperations.PopCount(candidates));
            if (count < batchSize) inputs.Clear();
            uint batch = candidates;
            for (int i = 0; i < count; i++)
            {
                int index = BitOperations.TrailingZeroCount(candidates);
                candidates &= candidates - 1;
                Span<byte> block = inputs.Slice(i * inputLength, inputLength);
                children[index]!.CopyTo(block);
                if (!Avx512F.IsSupported)
                {
                    block[KeccakHash.Hash532InputLength..].Clear();
                    block[KeccakHash.Hash532InputLength] = 0x01;
                    block[^1] = 0x80;
                }
            }
            if (Avx512F.IsSupported)
                KeccakHash.ComputeHash532Bytes8Avx512(ref inputs[0], ref hashes[0]);
            else
                KeccakHash.ComputePaddedMultiBlocks4Avx2(ref inputs[0], inputLength, ref hashes[0]);
            BatchedBranchHashes += count;
            for (int i = 0; i < count; i++)
            {
                int index = BitOperations.TrailingZeroCount(batch);
                batch &= batch - 1;
                views[index] = FromRlp(children[index], new ValueHash256(hashes.Slice(i * Hash256.Size, Hash256.Size)));
            }
        } while (BitOperations.PopCount(candidates) >= MinHashBatchSize);
        return candidates;
    }

    public static NodeView Combine(ReadOnlySpan<NodeView> children)
    {
        int present = 0;
        int only = -1;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (children[index].Kind == NodeViewKind.Empty) continue;

            present++;
            only = index;
        }

        if (present == 0) return NodeView.Empty;

        if (present >= 2)
        {
            ChildVector references = ChildVector.Rent();
            for (int index = 0; index < BranchRlp.ChildCount; index++) children[index].WriteReference(references, index);
            return NodeView.Branch(references);
        }

        NodeView child = children[only];
        if (child.Kind == NodeViewKind.Branch)
        {
            Span<byte> reference = stackalloc byte[Hash256.Size];
            child.CopyReferenceTo(reference);
            return NodeView.Extension([(byte)only], reference[..child.ReferenceLength]);
        }

        Span<byte> merged = stackalloc byte[MaxNibbles + 1];
        merged[0] = (byte)only;
        bool isLeaf = DecodeShortNode(child.Rlp, merged[1..], out int nibbleCount, out ReadOnlySpan<byte> payload);
        ReadOnlySpan<byte> nibbles = merged[..(nibbleCount + 1)];
        return isLeaf ? NodeView.Leaf(nibbles, payload) : NodeView.Extension(nibbles, payload);
    }

    private static NodeView AsBranch(ReadOnlySpan<byte> rlp, Hash256? knownHash)
    {
        ChildVector children = ChildVector.Rent();
        try
        {
            BranchRlp.ReadChildren(rlp, children);
        }
        catch
        {
            ChildVector.Return(children);
            throw;
        }

        return NodeView.Branch(children, rlp, knownHash?.ValueHash256);
    }

    private static bool DecodeShortNode(ReadOnlySpan<byte> rlp, Span<byte> nibbles, out int nibbleCount, out ReadOnlySpan<byte> payload)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
        bool isLeaf = (hexPrefix[0] & 0x20) != 0;
        nibbleCount = 0;
        if ((hexPrefix[0] & 0x10) != 0) nibbles[nibbleCount++] = (byte)(hexPrefix[0] & 0x0F);
        for (int index = 1; index < hexPrefix.Length; index++)
        {
            nibbles[nibbleCount++] = (byte)(hexPrefix[index] >> 4);
            nibbles[nibbleCount++] = (byte)(hexPrefix[index] & 0x0F);
        }

        if (isLeaf)
        {
            payload = reader.DecodeByteArraySpan();
            return true;
        }

        if (reader.IsSequenceNext())
        {
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            payload = reader.Read(prefixLength + contentLength);
            return false;
        }

        payload = reader.DecodeByteArraySpan();
        return false;
    }
}
