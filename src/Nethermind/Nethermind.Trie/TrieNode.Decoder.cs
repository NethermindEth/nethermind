// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Diagnostics;
using System.Linq;
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
            public CappedArray<byte> Encode(ITrieNodeResolver tree, TrieNode? item)
            {
                Metrics.TreeNodeRlpEncodings++;

                if (item is null)
                {
                    throw new TrieException("An attempt was made to RLP encode a null node.");
                }

                return item.NodeType switch
                {
                    NodeType.Branch => RlpEncodeBranch(tree, item),
                    NodeType.Extension => EncodeExtension(tree, item),
                    NodeType.Leaf => EncodeLeaf(item),
                    _ => throw new TrieException($"An attempt was made to encode a trie node of type {item.NodeType}")
                };
            }

            [SkipLocalsInit]
            private static CappedArray<byte> EncodeExtension(ITrieNodeResolver tree, TrieNode item)
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

                nodeRef.ResolveKey(tree, false);

                int contentLength = Rlp.LengthOf(keyBytes) + (nodeRef.Keccak is null ? nodeRef.FullRlp.Value.Length : Rlp.LengthOfKeccakRlp);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new(totalLength);
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
                    rlpStream.Write(nodeRef.FullRlp.Value.AsSpan());
                }
                else
                {
                    rlpStream.Encode(nodeRef.Keccak);
                }

                return rlpStream.CappedData;
            }

            [SkipLocalsInit]
            private static CappedArray<byte> EncodeLeaf(TrieNode node)
            {
                if (node.Key is null)
                {
                    throw new TrieException($"Hex prefix of a leaf node is null at node {node.Keccak}");
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
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(node.Value);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                if (rentedBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(rentedBuffer);
                }
                rlpStream.Encode(node.Value);
                return rlpStream.Data.ToCappedArray().Value;
            }

            private static CappedArray<byte> RlpEncodeBranch(ITrieNodeResolver tree, TrieNode item)
            {
                int valueRlpLength = AllowBranchValues ? Rlp.LengthOf(item.Value) : 1;
                int contentLength = valueRlpLength + GetChildrenRlpLength(tree, item);
                int sequenceLength = Rlp.LengthOfSequence(contentLength);
                byte[] result = new byte[sequenceLength];
                Span<byte> resultSpan = result.AsSpan();
                int position = Rlp.StartSequence(result, 0, contentLength);
                WriteChildrenRlp(tree, item, resultSpan.Slice(position, contentLength - valueRlpLength));
                position = sequenceLength - valueRlpLength;
                if (AllowBranchValues)
                {
                    Rlp.Encode(result, position, item.Value);
                }
                else
                {
                    result[position] = 128;
                }

                return result.ToCappedArray().Value;
            }

            private static int GetChildrenRlpLength(ITrieNodeResolver tree, TrieNode item)
            {
                int totalLength = 0;
                item.InitData();
                item.SeekChild(0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    if (item._rlpStream is not null && item._data![i] is null)
                    {
                        (int prefixLength, int contentLength) = item._rlpStream.PeekPrefixAndContentLength();
                        totalLength += prefixLength + contentLength;
                    }
                    else
                    {
                        if (ReferenceEquals(item._data![i], _nullNode) || item._data[i] is null)
                        {
                            totalLength++;
                        }
                        else if (item._data[i] is Keccak)
                        {
                            totalLength += Rlp.LengthOfKeccakRlp;
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode)item._data[i];
                            childNode!.ResolveKey(tree, false);
                            totalLength += childNode.Keccak is null ? childNode.FullRlp!.Value.Length : Rlp.LengthOfKeccakRlp;
                        }
                    }

                    item._rlpStream?.SkipItem();
                }

                return totalLength;
            }

            private static void WriteChildrenRlp(ITrieNodeResolver tree, TrieNode item, Span<byte> destination)
            {
                int position = 0;
                RlpStream rlpStream = item._rlpStream;
                item.InitData();
                item.SeekChild(0);
                for (int i = 0; i < BranchesCount; i++)
                {
                    if (rlpStream is not null && item._data![i] is null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        Span<byte> nextItem = rlpStream.Data.AsSpan(rlpStream.Position, length);
                        nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                        position += nextItem.Length;
                        rlpStream.SkipItem();
                    }
                    else
                    {
                        rlpStream?.SkipItem();
                        if (ReferenceEquals(item._data![i], _nullNode) || item._data[i] is null)
                        {
                            destination[position++] = 128;
                        }
                        else if (item._data[i] is Keccak)
                        {
                            position = Rlp.Encode(destination, position, (item._data[i] as Keccak)!.Bytes);
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode)item._data[i];
                            childNode!.ResolveKey(tree, false);
                            if (childNode.Keccak is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp!.Value.AsSpan();
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
