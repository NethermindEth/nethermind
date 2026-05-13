// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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

        private class TrieNodeDecoder
        {
            [SkipLocalsInit]
            public static CappedArray<byte> EncodeExtension(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                Metrics.TreeNodeRlpEncodings++;

                Debug.Assert(item.NodeType == NodeType.Extension,
                    $"Node passed to {nameof(EncodeExtension)} is {item.NodeType}");
                Debug.Assert(item.Key is not null,
                    "Extension key is null when encoding");

                byte[] hexPrefix = item.Key;
                int hexLength = HexPrefix.ByteLength(hexPrefix);
                byte[]? rentedBuffer = hexLength > StackallocByteThreshold
                    ? ArrayPool<byte>.Shared.Rent(hexLength)
                    : null;

                Span<byte> keyBytes = (rentedBuffer is null
                    ? stackalloc byte[StackallocByteThreshold]
                    : rentedBuffer)[..hexLength];

                HexPrefix.CopyToSpan(hexPrefix, isLeaf: false, keyBytes);

                // Fast path: child was unresolved (slot null, hash held in parent _rlpArray) —
                // encode the hash directly via TryGetChildHash without materializing a TrieNode.
                TrieNode? nodeRef = null;
                ValueHash256 childKeccak = default;
                bool hasChildKeccak = false;
                TrieNode? extChild = item.GetSlotRef(0);
                if (extChild is null && item.TryGetChildHash(0, out ValueHash256 hashFromRlp))
                {
                    childKeccak = hashFromRlp;
                    hasChildKeccak = true;
                }
                else
                {
                    int previousLength = item.AppendChildPath(ref path, 0);
                    nodeRef = item.GetChildWithChildPath(tree, ref path, 0);
                    Debug.Assert(nodeRef is not null,
                        "Extension child is null when encoding.");

                    nodeRef.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                    path.TruncateMut(previousLength);

                    hasChildKeccak = nodeRef.TryGetKeccak(out childKeccak);
                }

                int contentLength = Rlp.LengthOf(keyBytes) + (hasChildKeccak ? Rlp.LengthOfKeccakRlp : nodeRef!.FullRlp.Length);
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = bufferPool.SafeRent(totalLength);
                Span<byte> destination = data.AsSpan();
                int position = Rlp.StartSequence(destination, 0, contentLength);
                position = Rlp.Encode(destination, position, keyBytes);

                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                if (hasChildKeccak)
                {
                    Rlp.Encode(destination, position, in childKeccak);
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
                Metrics.TreeNodeRlpEncodings++;

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

            public static CappedArray<byte> RlpEncodeBranch(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool, bool canBeParallel)
            {
                Metrics.TreeNodeRlpEncodings++;

                const int valueRlpLength = 1;
                int contentLength = valueRlpLength + (UseParallel(canBeParallel, item) ? GetChildrenRlpLengthForBranchParallel(tree, ref path, item, pool, canBeParallel) : GetChildrenRlpLengthForBranch(tree, ref path, item, pool, canBeParallel));
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRent(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                WriteChildrenRlpBranch(tree, ref path, item, resultSpan.Slice(position, contentLength - valueRlpLength), pool, canBeParallel);
                position = sequenceLength - valueRlpLength;
                resultSpan[position] = 128;

                return result;

                static bool UseParallel(bool canBeParallel, TrieNode item)
                {
                    if (Environment.ProcessorCount <= 1 || !canBeParallel)
                    {
                        return false;
                    }

                    const int MinChildrenForParallel = 4;
                    int nonNullChildren = 0;
                    for (int i = 0; i < BranchesCount; i++)
                    {
                        TrieNode? data = item.GetSlotRef(i);
                        if (data is not null && !ReferenceEquals(data, NullNode) && ++nonNullChildren >= MinChildrenForParallel)
                        {
                            return true;
                        }
                    }

                    return false;
                }
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

            private static int GetChildrenRlpLengthForBranchNonRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        TrieNode? data = state.item.GetSlotRef(i);
                        if (data is null || ReferenceEquals(data, NullNode))
                        {
                            // Null slot on a non-RLP branch is impossible (no parent RLP to
                            // decode from). Treat as empty.
                            state.local++;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            path.AppendMut(i);
                            data.ResolveKey(state.tree, ref path, bufferPool: state.bufferPool, canBeParallel: state.canBeParallel);
                            state.local += data.HasKeccak ? Rlp.LengthOfKeccakRlp : data.FullRlp.Length;
                        }

                        return state;
                    },
                    state =>
                    {
                        Interlocked.Add(ref totalLength, state.local);
                    });

                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null || ReferenceEquals(data, NullNode))
                    {
                        totalLength++;
                    }
                    else
                    {
                        path.AppendMut(i);
                        data.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                        path.TruncateOne();
                        totalLength += data.HasKeccak ? Rlp.LengthOfKeccakRlp : data.FullRlp.Length;
                    }
                }
                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchRlpParallel(ITrieNodeResolver tree, TreePath rootPath, TrieNode item, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int totalLength = 0;
                ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    (local: 0, item, tree, bufferPool, rootPath, canBeParallel),
                    static (i, state) =>
                    {
                        ValueRlpStream rlpStream = state.item.RlpStream;
                        state.item.SeekChild(ref rlpStream, i);
                        TrieNode? data = state.item.GetSlotRef(i);
                        if (data is null)
                        {
                            // Slot unresolved; canonical encoding lives in the parent RLP.
                            state.local += rlpStream.PeekNextRlpLength();
                        }
                        else if (ReferenceEquals(data, NullNode))
                        {
                            state.local++;
                        }
                        else
                        {
                            TreePath path = state.rootPath;
                            path.AppendMut(i);
                            data.ResolveKey(state.tree, ref path, bufferPool: state.bufferPool, canBeParallel: state.canBeParallel);
                            state.local += data.HasKeccak ? Rlp.LengthOfKeccakRlp : data.FullRlp.Length;
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
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null)
                    {
                        // Unresolved slot; canonical bytes live in the parent RLP at the
                        // current stream position. Take the same length the source has.
                        int length = rlpStream.PeekNextRlpLength();
                        totalLength += length;
                        rlpStream.SkipBytes(length);
                    }
                    else
                    {
                        if (ReferenceEquals(data, NullNode))
                        {
                            totalLength++;
                        }
                        else
                        {
                            path.AppendMut(i);
                            data.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                            path.TruncateOne();
                            totalLength += data.HasKeccak ? Rlp.LengthOfKeccakRlp : data.FullRlp.Length;
                        }

                        rlpStream.SkipItem();
                    }
                }

                return totalLength;
            }

            private static void WriteChildrenRlpBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                // Tail call optimized.
                if (item.HasRlp)
                {
                    WriteChildrenRlpBranchRlp(tree, ref path, item, destination, bufferPool, canBeParallel);
                }
                else
                {
                    WriteChildrenRlpBranchNonRlp(tree, ref path, item, destination, bufferPool, canBeParallel);
                }
            }

            private static void WriteChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null || ReferenceEquals(data, NullNode))
                    {
                        destination[position++] = 128;
                    }
                    else
                    {
                        path.AppendMut(i);
                        data.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                        path.TruncateOne();

                        if (data.TryGetKeccak(out ValueHash256 childKeccak))
                        {
                            position = Rlp.Encode(destination, position, in childKeccak);
                        }
                        else
                        {
                            Span<byte> fullRlp = data.FullRlp.AsSpan();
                            fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                            position += fullRlp.Length;
                        }
                    }
                }
            }

            private static void WriteChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    TrieNode? data = item.GetSlotRef(i);
                    if (data is null)
                    {
                        // Unresolved slot: copy the canonical bytes from the parent's RLP.
                        int length = rlpStream.PeekNextRlpLength();
                        ReadOnlySpan<byte> nextItem = rlpStream.Data.Slice(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipBytes(length);
                    }
                    else
                    {
                        if (ReferenceEquals(data, NullNode))
                        {
                            destination[position++] = 128;
                        }
                        else
                        {
                            path.AppendMut(i);
                            data.ResolveKey(tree, ref path, bufferPool: bufferPool, canBeParallel: canBeParallel);
                            path.TruncateOne();

                            if (data.TryGetKeccak(out ValueHash256 childKeccak))
                            {
                                position = Rlp.Encode(destination, position, in childKeccak);
                            }
                            else
                            {
                                Span<byte> fullRlp = data.FullRlp.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                        }

                        rlpStream.SkipItem();
                    }
                }
            }
        }
    }
}
