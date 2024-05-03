// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Trie.Pruning;

[assembly: InternalsVisibleTo("Ethereum.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]

namespace Nethermind.Trie
{
    public partial class TrieNode
    {
        private const int StackallocByteThreshold = 256;

        private class TrieNodeDecoder
        {
            [SkipLocalsInit]
            public static CappedArray<byte> EncodeExtension(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? bufferPool)
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

                int previousLength = item.AppendChildPath(ref path, 0);
                TrieNode nodeRef = item.GetChildWithChildPath(tree, ref path, 0);
                Debug.Assert(nodeRef is not null,
                    "Extension child is null when encoding.");

                nodeRef.ResolveKey(tree, ref path, false, bufferPool: bufferPool);
                path.TruncateMut(previousLength);

                int contentLength = Rlp.LengthOf(keyBytes) + (nodeRef.Keccak is null ? nodeRef.FullRlp.Length : Rlp.LengthOfKeccakRlp);
                int totalLength = Rlp.LengthOfSequence(contentLength);

                CappedArray<byte> data = bufferPool.SafeRentBuffer(totalLength);
                RlpStream rlpStream = data.AsRlpStream();
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                if (nodeRef.Keccak is null)
                {
                    // I think it can only happen if we have a short extension to a branch with a short extension as the only child?
                    // so |
                    // so |
                    // so E - - - - - - - - - - - - - - -
                    // so |
                    // so |
                    rlpStream.Write(nodeRef.FullRlp.AsSpan());
                }
                else
                {
                    rlpStream.Encode(nodeRef.Keccak);
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

                CappedArray<byte> data = pool.SafeRentBuffer(totalLength);
                RlpStream rlpStream = data.AsRlpStream();
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                rlpStream.Encode(node.Value.AsSpan());
                return data;
            }

            [DoesNotReturn]
            [StackTraceHidden]
            private static void ThrowNullKey(TrieNode node)
            {
                throw new TrieException($"Hex prefix of a leaf node is null at node {node.Keccak}");
            }

            public static CappedArray<byte> RlpEncodeBranch(TrieNode item, ITrieNodeResolver tree, ref TreePath path, ICappedArrayPool? pool)
            {
                Metrics.TreeNodeRlpEncodings++;

                int valueRlpLength = AllowBranchValues ? Rlp.LengthOf(item.Value.AsSpan()) : 1;
                int contentLength = valueRlpLength + GetChildrenRlpLengthForBranch(tree, ref path, item, pool);
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRentBuffer(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                WriteChildrenRlpBranch(tree, ref path, item, resultSpan.Slice(position, contentLength - valueRlpLength), pool);
                position = sequenceLength - valueRlpLength;
                if (AllowBranchValues)
                {
                    Rlp.Encode(resultSpan, position, item.Value);
                }
                else
                {
                    result.AsSpan()[position] = 128;
                }

                return result;
            }

            private static int GetChildrenRlpLengthForBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool)
            {
                item.EnsureInitialized();
                // Tail call optimized.
                if (item.HasRlp)
                {
                    return GetChildrenRlpLengthForBranchRlp(tree, ref path, item, bufferPool);
                }
                else
                {
                    return GetChildrenRlpLengthForBranchNonRlp(tree, ref path, item, bufferPool);
                }
            }

            private static int GetChildrenRlpLengthForBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool bufferPool)
            {
                int totalLength = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._data[i];
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
                        TrieNode childNode = (TrieNode)data;
                        childNode.ResolveKey(tree, ref path, isRoot: false, bufferPool: bufferPool);
                        path.TruncateOne();
                        totalLength += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                    }
                }
                return totalLength;
            }

            private static int GetChildrenRlpLengthForBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, ICappedArrayPool? bufferPool)
            {
                int totalLength = 0;
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._data[i];
                    if (data is null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        totalLength += length;
                        rlpStream.SkipBytes(length);
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
                            childNode.ResolveKey(tree, ref path, isRoot: false, bufferPool: bufferPool);
                            path.TruncateOne();
                            totalLength += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }

                        rlpStream.SkipItem();
                    }
                }

                return totalLength;
            }

            private static void WriteChildrenRlpBranch(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool)
            {
                item.EnsureInitialized();
                // Tail call optimized.
                if (item.HasRlp)
                {
                    WriteChildrenRlpBranchRlp(tree, ref path, item, destination, bufferPool);
                }
                else
                {
                    WriteChildrenRlpBranchNonRlp(tree, ref path, item, destination, bufferPool);
                }
            }

            private static void WriteChildrenRlpBranchNonRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool)
            {
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._data[i];
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
                        childNode!.ResolveKey(tree, ref path, isRoot: false, bufferPool: bufferPool);
                        path.TruncateOne();

                        hash = childNode.Keccak;
                        if (hash is null)
                        {
                            Span<byte> fullRlp = childNode.FullRlp!.AsSpan();
                            fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                            position += fullRlp.Length;
                        }
                        else
                        {
                            position = Rlp.Encode(destination, position, hash);
                        }
                    }
                }
            }

            private static void WriteChildrenRlpBranchRlp(ITrieNodeResolver tree, ref TreePath path, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool)
            {
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                int position = 0;
                for (int i = 0; i < BranchesCount; i++)
                {
                    object data = item._data[i];
                    if (data is null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        Span<byte> nextItem = rlpStream.Data.AsSpan(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipBytes(length);
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
                            childNode!.ResolveKey(tree, ref path, isRoot: false, bufferPool: bufferPool);
                            path.TruncateOne();

                            hash = childNode.Keccak;
                            if (hash is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp!.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                            else
                            {
                                position = Rlp.Encode(destination, position, hash);
                            }
                        }

                        rlpStream.SkipItem();
                    }
                }
            }
        }
    }
}
