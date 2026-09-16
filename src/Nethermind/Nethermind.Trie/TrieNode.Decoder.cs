// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics.X86;
using System.Threading;
using Nethermind.Core.Buffers;
using Nethermind.Core.Cpu;
using Nethermind.Core.Crypto;
using Nethermind.Core.Threading;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        // Used to create the nibble key from bytes, and threshold before using ArrayPool for the key
        private const int StackallocByteThreshold = 384;
        private const int FullBranchRlpLength = 532;

        private class TrieNodeDecoder
        {
            private const int HashPairSize = 2;

            /// <summary>The children of a node already known to be a branch.</summary>
            /// <remarks>
            /// Walking the inline array keeps the encode passes below off <see cref="INodeData"/>'s
            /// indexer, which is an interface call per child. The type test is once per pass, and is a
            /// test rather than an unchecked cast because reading a smaller node's data as sixteen
            /// references would corrupt the heap instead of throwing.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ReadOnlySpan<object?> BranchChildren(TrieNode item) => Branch(item).Branches;

            /// <inheritdoc cref="BranchChildren"/>
            /// <remarks>
            /// <inheritdoc cref="BranchChildren" path="/remarks"/>
            /// A walk that also needs the child index takes the first element by reference and advances
            /// it, rather than indexing: measured on the guest, indexing the span costs the scale per
            /// child, +995,495 ziskemu steps a block across the four passes that do so.
            /// </remarks>
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static ref object? FirstBranchChild(TrieNode item) => ref Branch(item)[0];

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            private static BranchData Branch(TrieNode item) => item._nodeData as BranchData ?? ThrowNotABranch();

            [DoesNotReturn, StackTraceHidden]
            private static BranchData ThrowNotABranch() =>
                throw new TrieException("A node encoded as a branch does not hold branch data.");

            [SkipLocalsInit]
            public static CappedArray<byte> EncodeExtension(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                Metrics.IncrementTreeNodeRlpEncodings();

                Debug.Assert(item.NodeType == NodeType.Extension,
                    $"Node passed to {nameof(EncodeExtension)} is {item.NodeType}");
                Debug.Assert(item.Key is not null,
                    "Extension key is null when encoding");

                byte[] hexPrefix = item.Key!;
                int hexLength = HexPrefix.ByteLength(hexPrefix);
                byte[]? rentedBuffer = hexLength > StackallocByteThreshold
                    ? ArrayPool<byte>.Shared.Rent(hexLength)
                    : null;

                Span<byte> keyBytes = (rentedBuffer is null
                    ? stackalloc byte[StackallocByteThreshold]
                    : rentedBuffer)[..hexLength];

                HexPrefix.CopyToSpan(hexPrefix, isLeaf: false, keyBytes);

                // Fast path: child was unresolved to a Hash256 (e.g. by pruning) — encode the hash directly
                // without materializing a TrieNode via FindCachedOrUnknown + ResolveKey.
                TrieNode? nodeRef = null;
                Hash256? childKeccak;
                if (item._nodeData![0] is Hash256 dataKeccak)
                {
                    childKeccak = dataKeccak;
                }
                else
                {
                    int previousLength = item.AppendChildPath(ref path, 0);
                    nodeRef = item.GetChildWithChildPath(tree, ref path, 0);
                    Debug.Assert(nodeRef is not null,
                        "Extension child is null when encoding.");

                    nodeRef.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                    path.TruncateMut(previousLength);

                    childKeccak = nodeRef.Keccak;
                }

                int contentLength = Rlp.LengthOf(keyBytes) + (childKeccak is not null ? Rlp.LengthOfKeccakRlp : nodeRef!.FullRlp.Length);
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = bufferPool.SafeRent(totalLength);
                Span<byte> destination = data.AsSpan();
                int position = Rlp.StartSequence(destination, 0, contentLength);
                position = Rlp.Encode(destination, position, keyBytes);

                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                if (childKeccak is not null)
                {
                    Rlp.Encode(destination, position, childKeccak);
                }
                else
                {
                    // Inline child: happens with a short extension to a branch with a short extension as the only child
                    // so |
                    // so |
                    // so E - - - - - - - - - - - - - - -
                    // so |
                    // so |
                    nodeRef!.FullRlp.AsSpan().CopyTo(destination.Slice(position));
                }

                return data;
            }

            [SkipLocalsInit]
            public static CappedArray<byte> EncodeLeaf(TrieNode node, ICappedArrayPool? pool)
            {
                Metrics.IncrementTreeNodeRlpEncodings();

                if (node.Key is null)
                {
                    ThrowNullKey(node);
                }

                byte[] hexPrefix = node.Key;
                int hexLength = HexPrefix.ByteLength(hexPrefix);
                byte[]? rentedBuffer = hexLength > StackallocByteThreshold
                    ? ArrayPool<byte>.Shared.Rent(hexLength)
                    : null;

                Span<byte> keyBytes = (rentedBuffer is null
                    ? stackalloc byte[StackallocByteThreshold]
                    : rentedBuffer)[..hexLength];

                HexPrefix.CopyToSpan(hexPrefix, isLeaf: true, keyBytes);
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(node.Value.AsSpan());
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = pool.SafeRent(totalLength);
                Span<byte> destination = data.AsSpan();
                int position = Rlp.StartSequence(destination, 0, contentLength);
                position = Rlp.Encode(destination, position, keyBytes);

                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }

                Rlp.Encode(destination, position, node.Value.AsSpan());

                return data;
            }

            [DoesNotReturn, StackTraceHidden]
            private static void ThrowNullKey(TrieNode node) => throw new TrieException($"Hex prefix of a leaf node is null at node {node.Keccak}");

            /// <summary>Scratch for one branch's children, before the sequence header is known.</summary>
            /// <remarks>Sixteen children at most, each either a 33-byte hash item or a node small enough
            /// to be embedded, which the trie only does below 32 bytes. A struct local rather than a
            /// stackalloc, for the reason given on <c>KeccakHash</c>'s state buffer: localloc would pin
            /// the method at Tier0-FullOpts and add stack-probe overhead per call. Writes go through a
            /// span, so an over-long branch throws rather than running off the buffer.</remarks>
            [InlineArray(BranchesCount * Rlp.LengthOfKeccakRlp)]
            private struct BranchScratch
            {
                private byte _element0;
            }

            [SkipLocalsInit]
            public static CappedArray<byte> RlpEncodeBranch(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool, bool canBeParallel)
            {
                Metrics.IncrementTreeNodeRlpEncodings();

                const int valueRlpLength = 1;
                int contentLength;
                int sequenceLength;
                CappedArray<byte> result;
                Span<byte> resultSpan;
                int position;

                // The sequence header carries the children's length and CappedArray has no offset to write
                // it backwards into, so the length has to be known before the children can go in. Writing
                // them into a scratch buffer and copying them in behind the header trades the measuring
                // walk for one bounded copy. The walk is kept where it does something else as well:
                // spreading the children over cores, or collecting branch pairs for batched hashing.
                bool useParallel = UseParallel(canBeParallel, item);
                if (useParallel || (Avx512F.VL.IsSupported && HasBatchableChildPair(item)))
                {
                    contentLength = valueRlpLength + (useParallel
                        ? GetChildrenRlpLengthForBranchParallel(tree, ref path, item, pool, canBeParallel)
                        : GetChildrenRlpLengthForBranch(tree, ref path, item, pool, canBeParallel));
                    sequenceLength = Rlp.LengthOfSequence(contentLength);
                    result = pool.SafeRent(sequenceLength);
                    resultSpan = result.AsSpan();
                    position = Rlp.StartSequence(resultSpan, 0, contentLength);
                    WriteChildrenRlpBranch(tree, ref path, item, resultSpan.Slice(position, contentLength - valueRlpLength), pool, canBeParallel);
                    resultSpan[sequenceLength - valueRlpLength] = 128;

                    return result;
                }

                Unsafe.SkipInit(out BranchScratch scratch);
                Span<byte> children = scratch;
                int childrenLength = WriteChildrenRlpBranch(tree, ref path, item, children, pool, canBeParallel);
                contentLength = valueRlpLength + childrenLength;
                sequenceLength = Rlp.LengthOfSequence(contentLength);
                result = pool.SafeRent(sequenceLength);
                resultSpan = result.AsSpan();
                position = Rlp.StartSequence(resultSpan, 0, contentLength);
                children[..childrenLength].CopyTo(resultSpan[position..]);
                resultSpan[sequenceLength - valueRlpLength] = 128;

                return result;

                static bool UseParallel(bool canBeParallel, TrieNode item)
                {
                    if (RuntimeInformation.IsSingleProcessor || !canBeParallel)
                    {
                        return false;
                    }

                    const int MinChildrenForParallel = 4;
                    int nonNullChildren = 0;
                    foreach (object? data in BranchChildren(item))
                    {
                        if (data is not null && !ReferenceEquals(data, _nullNode) && ++nonNullChildren >= MinChildrenForParallel)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            /// <summary>Whether the measuring walk could pair up child hashes for <see cref="HashPreparedBranches" />.</summary>
            /// <remarks>Only a dirty branch child is a candidate, and a lone candidate is hashed on its own, so
            /// a branch without two of them gains nothing from the walk. The walk narrows the set further — a
            /// candidate whose RLP is not a full branch drops out — so an upper bound is all this needs to be.</remarks>
            private static bool HasBatchableChildPair(TrieNode item)
            {
                const int MinChildrenForBatchedHashing = 2;
                int candidates = 0;
                foreach (object? data in BranchChildren(item))
                {
                    if (data is TrieNode { IsBranch: true, Keccak: null } && ++candidates >= MinChildrenForBatchedHashing)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static void HashPreparedBranches(TrieNode item, ushort candidateMask)
            {
                int firstIndex = BitOperations.TrailingZeroCount(candidateMask);
                candidateMask ^= (ushort)(1 << firstIndex);
                if (candidateMask == 0)
                {
                    Unsafe.As<TrieNode>(item._nodeData![firstIndex])!.ResolvePreparedKey();
                    return;
                }

                HashPreparedBranchPairs(item, firstIndex, candidateMask);
            }

            [SkipLocalsInit]
            [MethodImpl(MethodImplOptions.NoInlining)]
            private static void HashPreparedBranchPairs(TrieNode item, int firstIndex, ushort candidateMask)
            {
                Span<byte> hashes = stackalloc byte[HashPairSize * Hash256.Size];

                while (true)
                {
                    int secondIndex = BitOperations.TrailingZeroCount(candidateMask);
                    candidateMask ^= (ushort)(1 << secondIndex);
                    TrieNode first = Unsafe.As<TrieNode>(item._nodeData![firstIndex])!;
                    TrieNode second = Unsafe.As<TrieNode>(item._nodeData[secondIndex])!;
                    CappedArray<byte> firstRlp = first.FullRlp;
                    CappedArray<byte> secondRlp = second.FullRlp;
                    if (firstRlp.Length != FullBranchRlpLength || secondRlp.Length != FullBranchRlpLength)
                    {
                        ThrowUnexpectedPreparedBranchLength();
                    }

                    Span<byte> rlp0 = firstRlp.AsSpan();
                    Span<byte> rlp1 = secondRlp.AsSpan();
                    KeccakHash.ComputeHash532Bytes2Avx512VL(ref rlp0[0], ref rlp1[0], ref hashes[0]);
                    ValueHash256 firstHash = new(hashes[..Hash256.Size]);
                    ValueHash256 secondHash = new(hashes[Hash256.Size..]);
                    first.SetPreparedKey(in firstHash);
                    second.SetPreparedKey(in secondHash);

                    if (candidateMask == 0)
                    {
                        return;
                    }

                    firstIndex = BitOperations.TrailingZeroCount(candidateMask);
                    candidateMask ^= (ushort)(1 << firstIndex);
                    if (candidateMask == 0)
                    {
                        Unsafe.As<TrieNode>(item._nodeData![firstIndex])!.ResolvePreparedKey();
                        return;
                    }
                }

                [DoesNotReturn, StackTraceHidden]
                static void ThrowUnexpectedPreparedBranchLength() =>
                    throw new TrieException("A prepared full branch changed before batched hashing.");
            }

            private static int GetChildrenRlpLengthForBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel) =>
                // Tail call optimized.
                item.HasRlp
                    ? GetChildrenRlpLengthForBranchRlp(tree, ref path, item, bufferPool, canBeParallel)
                    : GetChildrenRlpLengthForBranchNonRlp(tree, ref path, item, bufferPool, canBeParallel);

            private static int GetChildrenRlpLengthForBranchParallel(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel) =>
                // Tail call optimized.
                item.HasRlp
                    ? GetChildrenRlpLengthForBranchRlpParallel(tree, path, item, bufferPool, canBeParallel)
                    : GetChildrenRlpLengthForBranchNonRlpParallel(tree, path, item, bufferPool, canBeParallel);

            private static int GetChildrenRlpLengthForBranchNonRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsLogicalCores,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        object? data = state.item._nodeData![i];
                        if (ReferenceEquals(data, _nullNode) || data is null)
                        {
                            state.local++;
                        }
                        else if (data is Hash256)
                        {
                            state.local += Rlp.LengthOfKeccakRlp;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            path.AppendMut(i);
                            TrieNode childNode = Unsafe.As<TrieNode>(data);
                            childNode.ResolveKey(state.tree, ref path, bufferPool: state.bufferPool, canBeParallel: state.canBeParallel);
                            state.local += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }

                        return state;
                    },
                    state =>
                    {
                        Interlocked.Add(ref totalLength, state.local);
                    });

                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ushort candidateMask = 0;
                ref object? child = ref FirstBranchChild(item);
                for (int i = 0; i < BranchesCount; i++, child = ref Unsafe.Add(ref child, 1))
                {
                    object? data = child;
                    if (ReferenceEquals(data, _nullNode) || data is null)
                    {
                        totalLength++;
                    }
                    else if (data is Hash256)
                    {
                        totalLength += Rlp.LengthOfKeccakRlp;
                    }
                    else
                    {
                        path.AppendMut(i);
                        TrieNode childNode = Unsafe.As<TrieNode>(data);
                        if (Avx512F.VL.IsSupported && childNode is { IsBranch: true, Keccak: null })
                        {
                            CappedArray<byte> rlp = childNode.PrepareRlp(tree, ref path, bufferPool, canBeParallel);
                            if (rlp.Length == FullBranchRlpLength)
                            {
                                candidateMask |= (ushort)(1 << i);
                                totalLength += Rlp.LengthOfKeccakRlp;
                            }
                            else
                            {
                                childNode.ResolvePreparedKey(in rlp);
                                totalLength += childNode.Keccak is null ? rlp.Length : Rlp.LengthOfKeccakRlp;
                            }
                        }
                        else
                        {
                            childNode.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                            totalLength += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }
                        path.TruncateOne();
                    }
                }

                if (candidateMask != 0)
                {
                    HashPreparedBranches(item, candidateMask);
                }

                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsLogicalCores,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        object? data = BranchChildren(state.item)[i];
                        if (data is null)
                        {
                            LiteRlpReader nodeRlp = new(state.item.FullRlp);
                            int cursor = state.item.SeekChildPosition(nodeRlp, i);
                            state.local += nodeRlp.PeekNextRlpLength(cursor);
                        }
                        else if (ReferenceEquals(data, _nullNode))
                        {
                            state.local++;
                        }
                        else if (data is Hash256)
                        {
                            state.local += Rlp.LengthOfKeccakRlp;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            path.AppendMut(i);
                            Debug.Assert(data is TrieNode, "Data is not TrieNode");
                            TrieNode childNode = Unsafe.As<TrieNode>(data);
                            childNode.ResolveKey(state.tree, ref path, bufferPool: state.bufferPool, canBeParallel: state.canBeParallel);
                            state.local += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }

                        return state;
                    },
                    state =>
                    {
                        Interlocked.Add(ref totalLength, state.local);
                    });

                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ushort candidateMask = 0;
                LiteRlpReader nodeRlp = new(item.FullRlp);
                int cursor = item.SeekChildPosition(nodeRlp, 0);
                ref object? child = ref FirstBranchChild(item);
                for (int i = 0; i < BranchesCount; i++, child = ref Unsafe.Add(ref child, 1))
                {
                    object? data = child;
                    if (data is null)
                    {
                        int length = nodeRlp.PeekNextRlpLength(cursor);
                        totalLength += length;
                        cursor += length;
                    }
                    else
                    {
                        if (ReferenceEquals(data, _nullNode) || data is null)
                        {
                            totalLength++;
                        }
                        else if (data is Hash256)
                        {
                            totalLength += Rlp.LengthOfKeccakRlp;
                        }
                        else
                        {
                            path.AppendMut(i);
                            Debug.Assert(data is TrieNode, "Data is not TrieNode");
                            TrieNode childNode = Unsafe.As<TrieNode>(data);
                            if (Avx512F.VL.IsSupported && childNode is { IsBranch: true, Keccak: null })
                            {
                                CappedArray<byte> rlp = childNode.PrepareRlp(tree, ref path, bufferPool, canBeParallel);
                                if (rlp.Length == FullBranchRlpLength)
                                {
                                    candidateMask |= (ushort)(1 << i);
                                    totalLength += Rlp.LengthOfKeccakRlp;
                                }
                                else
                                {
                                    childNode.ResolvePreparedKey(in rlp);
                                    totalLength += childNode.Keccak is null ? rlp.Length : Rlp.LengthOfKeccakRlp;
                                }
                            }
                            else
                            {
                                childNode.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                                totalLength += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                            }
                            path.TruncateOne();
                        }

                        nodeRlp.SkipItem(ref cursor);
                    }
                }

                if (candidateMask != 0)
                {
                    HashPreparedBranches(item, candidateMask);
                }

                return totalLength;
            }

            /// <summary>Writes a branch's sixteen children into <paramref name="destination" />, each as a hash
            /// item or an embedded node.</summary>
            /// <returns>The number of bytes written.</returns>
            private static int WriteChildrenRlpBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel) =>
                // Tail call optimized.
                item.HasRlp
                    ? WriteChildrenRlpBranchRlp(tree, ref path, item, destination, bufferPool, canBeParallel)
                    : WriteChildrenRlpBranchNonRlp(tree, ref path, item, destination, bufferPool, canBeParallel);

            /// <inheritdoc cref="WriteChildrenRlpBranch" />
            private static int WriteChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int position = 0;
                ref object? child = ref FirstBranchChild(item);
                for (int i = 0; i < BranchesCount; i++, child = ref Unsafe.Add(ref child, 1))
                {
                    object? data = child;
                    if (ReferenceEquals(data, _nullNode) || data is null)
                    {
                        destination[position++] = 128;
                    }
                    else if (data is Hash256 hash)
                    {
                        position = Rlp.Encode(destination, position, hash);
                    }
                    else
                    {
                        path.AppendMut(i);
                        Debug.Assert(data is TrieNode, "Data is not TrieNode");
                        TrieNode childNode = Unsafe.As<TrieNode>(data);
                        childNode!.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                        path.TruncateOne();

                        Hash256? childHash = childNode.Keccak;
                        if (childHash is null)
                        {
                            Span<byte> fullRlp = childNode.FullRlp.AsSpan();
                            fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                            position += fullRlp.Length;
                        }
                        else
                        {
                            position = Rlp.Encode(destination, position, childHash);
                        }
                    }
                }

                return position;
            }

            /// <inheritdoc cref="WriteChildrenRlpBranch" />
            private static int WriteChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                LiteRlpReader nodeRlp = new(item.FullRlp);
                ReadOnlySpan<byte> nodeRlpData = nodeRlp.Data;
                if (nodeRlpData.Length == FullBranchRlpLength && destination.Length >= BranchesCount * Rlp.LengthOfKeccakRlp
                    && TryPatchFullBranch(tree, ref path, item, nodeRlpData, destination, bufferPool, canBeParallel))
                {
                    return BranchesCount * Rlp.LengthOfKeccakRlp;
                }
                int cursor = item.SeekChildPosition(nodeRlp, 0);
                int position = 0;
                // Unchanged children are consecutive bytes of the old RLP, so a run of them is one
                // copy rather than one per child. Most branches change a single child, so this turns
                // sixteen short copies into two.
                int runStart = -1;
                int runLength = 0;
                ref object? child = ref FirstBranchChild(item);
                for (int i = 0; i < BranchesCount; i++, child = ref Unsafe.Add(ref child, 1))
                {
                    object? data = child;
                    if (data is null)
                    {
                        int length = nodeRlp.PeekNextRlpLength(cursor);
                        if (runStart < 0) runStart = cursor;
                        runLength += length;
                        cursor += length;
                    }
                    else
                    {
                        if (runStart >= 0)
                        {
                            nodeRlp.Data.Slice(runStart, runLength).CopyTo(destination.Slice(position, runLength));
                            position += runLength;
                            runStart = -1;
                            runLength = 0;
                        }

                        if (ReferenceEquals(data, _nullNode) || data is null)
                        {
                            destination[position++] = 128;
                        }
                        else if (data is Hash256 hash)
                        {
                            position = Rlp.Encode(destination, position, hash);
                        }
                        else
                        {
                            path.AppendMut(i);
                            Debug.Assert(data is TrieNode, "Data is not TrieNode");
                            TrieNode childNode = Unsafe.As<TrieNode>(data);
                            childNode!.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                            path.TruncateOne();

                            Hash256? childHash = childNode.Keccak;
                            if (childHash is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                            else
                            {
                                position = Rlp.Encode(destination, position, childHash);
                            }
                        }

                        nodeRlp.SkipItem(ref cursor);
                    }
                }

                if (runStart >= 0)
                {
                    nodeRlp.Data.Slice(runStart, runLength).CopyTo(destination.Slice(position, runLength));
                    position += runLength;
                }

                return position;
            }

            private static bool TryPatchFullBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item,
                ReadOnlySpan<byte> nodeRlp, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                // Nethermind branches have an empty value, so a canonical 532-byte branch has sixteen hash children.
                Debug.Assert(nodeRlp[^1] == 128);
                nodeRlp.Slice(3, BranchesCount * Rlp.LengthOfKeccakRlp).CopyTo(destination);
                ref object? child = ref FirstBranchChild(item);
                for (int i = 0; i < BranchesCount; i++, child = ref Unsafe.Add(ref child, 1))
                {
                    object? data = child;
                    if (data is null) continue;
                    if (ReferenceEquals(data, _nullNode)) return false;
                    Hash256? hash = data as Hash256;
                    if (hash is null)
                    {
                        TrieNode childNode = (TrieNode)data;
                        path.AppendMut(i);
                        childNode.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                        path.TruncateOne();
                        hash = childNode.Keccak;
                        if (hash is null) return false;
                    }
                    Rlp.Encode(destination, i * Rlp.LengthOfKeccakRlp, hash);
                }
                return true;
            }
        }
    }
}
