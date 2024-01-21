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
            public static CappedArray<byte> Encode(ITrieNodeResolver tree, TrieNode? item, ICappedArrayPool? bufferPool)
            {
                Metrics.TreeNodeRlpEncodings++;

                if (item is null)
                {
                    ThrowNullNode();
                }

                return item.NodeType switch
                {
                    NodeType.Branch => RlpEncodeBranch(tree, item, bufferPool),
                    NodeType.Extension => EncodeExtension(tree, item, bufferPool),
                    NodeType.Leaf => EncodeLeaf(item, bufferPool),
                    _ => ThrowUnhandledNodeType(item)
                };

                [DoesNotReturn]
                [StackTraceHidden]
                static void ThrowNullNode()
                {
                    throw new TrieException("An attempt was made to RLP encode a null node.");
                }

                [DoesNotReturn]
                [StackTraceHidden]
                static CappedArray<byte> ThrowUnhandledNodeType(TrieNode item)
                {
                    throw new TrieException($"An attempt was made to encode a trie node of type {item.NodeType}");
                }
            }

            [SkipLocalsInit]
            private static CappedArray<byte> EncodeExtension(ITrieNodeResolver tree, TrieNode item, ICappedArrayPool? bufferPool)
            {
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

                TrieNode nodeRef = item.GetChild(tree, 0);
                Debug.Assert(nodeRef is not null,
                    "Extension child is null when encoding.");

                nodeRef.ResolveKey(tree, false, bufferPool: bufferPool);

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
            private static CappedArray<byte> EncodeLeaf(TrieNode node, ICappedArrayPool? pool)
            {
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

            private static CappedArray<byte> RlpEncodeBranch(ITrieNodeResolver tree, TrieNode item, ICappedArrayPool? pool)
            {
                int valueRlpLength = AllowBranchValues ? Rlp.LengthOf(item.Value.AsSpan()) : 1;
                int contentLength = valueRlpLength + GetChildrenRlpLength(tree, item, pool);
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                CappedArray<byte> result = pool.SafeRentBuffer(sequenceLength);
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(resultSpan, 0, contentLength);
                WriteChildrenRlp(tree, item, resultSpan.Slice(position, contentLength - valueRlpLength), pool);
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

            private static int GetChildrenRlpLength(ITrieNodeResolver tree, TrieNode item, ICappedArrayPool? bufferPool)
            {
                int totalLength = 0;
                item.InitData();
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    bool isRlp = rlpStream.IsNotNull;
                    object data = item._data[i];
                    if (rlpStream.IsNotNull && data is null)
                    {
                        (int prefixLength, int contentLength) = rlpStream.PeekPrefixAndContentLength();
                        totalLength += prefixLength + contentLength;
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
                            TrieNode childNode = (TrieNode)data;
                            childNode!.ResolveKey(tree, false, bufferPool: bufferPool);
                            totalLength += childNode.Keccak is null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                        }
                    }

                    if (isRlp) rlpStream.SkipItem();
                }

                return totalLength;
            }

            private static void WriteChildrenRlp(ITrieNodeResolver tree, TrieNode item, Span<byte> destination, ICappedArrayPool? bufferPool)
            {
                int position = 0;
                item.InitData();
                ValueRlpStream rlpStream = item.RlpStream;
                item.SeekChild(ref rlpStream, 0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    bool isRlp = rlpStream.IsNotNull;
                    object data = item._data[i];
                    if (isRlp && data is null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        Span<byte> nextItem = rlpStream.Data.AsSpan(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipItem();
                    }
                    else
                    {
                        if (isRlp) rlpStream.SkipItem();
                        if (ReferenceEquals(data, _nullNode) || data is null)
                        {
                            destination[position++] = 128;
                        }
                        else if (data is Hash256)
                        {
                            position = Rlp.Encode(destination, position, (data as Hash256)!.Bytes);
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode)data;
                            childNode!.ResolveKey(tree, false, bufferPool: bufferPool);
                            if (childNode.Keccak is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp!.AsSpan();
                                fullRlp.CopyTo(destination.Slice(position, fullRlp.Length));
                                position += fullRlp.Length;
                            }
                            else
                            {
                                position = Rlp.Encode(destination, position, childNode.Keccak.Bytes);
                            }
                        }
                    }
                }
            }
        }
    }
}
