// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
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
                Metrics.IncrementTreeNodeRlpEncodings();

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

                // Fast path: child was unresolved to a Hash256 (e.g. by pruning) — encode the hash directly
                // without materializing a TrieNode via FindCachedOrUnknown + ResolveKey.
                TrieNode? nodeRef = null;
                if (item._nodeData[0] is not Hash256 childKeccak)
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

            public static CappedArray<byte> RlpEncodeBranch(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool, bool canBeParallel)
            {
                Metrics.IncrementTreeNodeRlpEncodings();

                const int valueRlpLength = 1;
                bool useParallel = UseParallel(canBeParallel, item);
                if (useParallel)
                {
                    ResolveChildrenForBranchParallel(tree, ref path, item, pool);
                }

                // A child reference is empty, inline (< 32 bytes), or a 33-byte hash RLP.
                const int maxChildrenRlpLength = BranchesCount * Rlp.LengthOfKeccakRlp;
                Span<byte> childrenRlp = stackalloc byte[maxChildrenRlpLength];
                int childrenRlpLength = WriteChildrenRlpBranch(
                    tree,
                    ref path,
                    item,
                    childrenRlp,
                    pool,
                    canBeParallel: canBeParallel && !useParallel);
                int contentLength = valueRlpLength + childrenRlpLength;
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRent(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                childrenRlp[..childrenRlpLength].CopyTo(resultSpan[position..]);
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
                        object? data = item._nodeData[i];
                        if (data is not null && !ReferenceEquals(data, _nullNode) && ++nonNullChildren >= MinChildrenForParallel)
                        {
                            return true;
                        }
                    }

                    return false;
                }
            }

            // The parent already distributes the complete branch frontier, so each worker resolves its subtree serially.
            private static void ResolveChildrenForBranchParallel(ITrieNodeResolver tree, ref TreePath rootPath, TrieNode item, ICappedArrayPool? bufferPool) => ParallelUnbalancedWork.For(0, BranchesCount, RuntimeInformation.ParallelOptionsPhysicalCoresUpTo16,
                    (item, tree, bufferPool, rootPath),
                    static (i, state) =>
                    {
                        object? data = state.item._nodeData[i];
                        if (data is not null && !ReferenceEquals(data, _nullNode) && data is not Hash256)
                        {
                            TreePath path = state.rootPath;
                            path.AppendMut(i);
                            Debug.Assert(data is TrieNode, "Data is not TrieNode");
                            TrieNode childNode = Unsafe.As<TrieNode>(data);
                            childNode.ResolveKey(state.tree, ref path, bufferPool: state.bufferPool, canBeParallel: false);
                        }

                        return state;
                    },
                    static _ => { });

            private static int WriteChildrenRlpBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel) =>
                // Tail call optimized.
                item.HasRlp
                    ? WriteChildrenRlpBranchRlp(tree, ref path, item, destination, bufferPool, canBeParallel)
                    : WriteChildrenRlpBranchNonRlp(tree, ref path, item, destination, bufferPool, canBeParallel);

            private static int WriteChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._nodeData[i];
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

                        hash = childNode.Keccak;
                        if (hash is null)
                        {
                            Span<byte> fullRlp = childNode.FullRlp.AsSpan();
                            fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                            position += fullRlp.Length;
                        }
                        else
                        {
                            position = Rlp.Encode(destination, position, hash);
                        }
                    }
                }

                return position;
            }

            private static int WriteChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool, bool canBeParallel)
            {
                RlpReader rlpReader = item.RlpReader;
                item.SeekChild(ref rlpReader, 0);
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._nodeData[i];
                    if (data is null)
                    {
                        int length = rlpReader.PeekNextRlpLength();
                        ReadOnlySpan<byte> nextItem = rlpReader.Data.Slice(rlpReader.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpReader.SkipBytes(length);
                    }
                    else
                    {
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

                            hash = childNode.Keccak;
                            if (hash is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                            else
                            {
                                position = Rlp.Encode(destination, position, hash);
                            }
                        }

                        rlpReader.SkipItem();
                    }
                }

                return position;
            }
        }
    }
}
