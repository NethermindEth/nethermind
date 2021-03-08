//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
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
        private class TrieNodeDecoder
        {
            public byte[] Encode(ITrieNodeResolver tree, TrieNode? item)
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

            private static byte[] EncodeExtension(ITrieNodeResolver tree, TrieNode item)
            {
                Debug.Assert(item.NodeType == NodeType.Extension,
                    $"Node passed to {nameof(EncodeExtension)} is {item.NodeType}");
                Debug.Assert(item.Key != null,
                    "Extension key is null when encoding");
                
                byte[] keyBytes = item.Key.ToBytes();
                TrieNode nodeRef = item.GetChild(tree, 0);
                Debug.Assert(nodeRef != null,
                    "Extension child is null when encoding.");
                
                nodeRef.ResolveKey(tree, false);

                int contentLength = Rlp.LengthOf(keyBytes) + (nodeRef.Keccak is null ? nodeRef.FullRlp.Length : Rlp.LengthOfKeccakRlp);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                if (nodeRef.Keccak is null)
                {
                    // I think it can only happen if we have a short extension to a branch with a short extension as the only child?
                    // so |
                    // so |
                    // so E - - - - - - - - - - - - - - - 
                    // so |
                    // so |
                    rlpStream.Write(nodeRef.FullRlp);
                }
                else
                {
                    rlpStream.Encode(nodeRef.Keccak);
                }

                return rlpStream.Data;
            }

            private static byte[] EncodeLeaf(TrieNode node)
            {
                if (node.Key is null)
                {
                    throw new TrieException($"Hex prefix of a leaf node is null at node {node.Keccak}");
                }

                byte[] keyBytes = node.Key.ToBytes();
                int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(node.Value);
                int totalLength = Rlp.LengthOfSequence(contentLength);
                RlpStream rlpStream = new(totalLength);
                rlpStream.StartSequence(contentLength);
                rlpStream.Encode(keyBytes);
                rlpStream.Encode(node.Value);
                return rlpStream.Data;
            }

            private static byte[] RlpEncodeBranch(ITrieNodeResolver tree, TrieNode item)
            {
                int valueRlpLength = AllowBranchValues ? Rlp.LengthOf(item.Value) : 1;
                int contentLength = valueRlpLength + GetChildrenRlpLength(tree, item);
                int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
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

                return result;
            }

            private static int GetChildrenRlpLength(ITrieNodeResolver tree, TrieNode item)
            {
                int totalLength = 0;
                item.InitData();
                item.SeekChild(0);
                for (int i = 0; i < 16; i++)
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
                        else if(item._data[i] is Keccak)
                        {
                            totalLength += Rlp.LengthOfKeccakRlp;
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode) item._data[i];
                            childNode!.ResolveKey(tree, false);
                            totalLength += childNode.Keccak is null ? childNode.FullRlp!.Length : Rlp.LengthOfKeccakRlp;
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
                for (int i = 0; i < 16; i++)
                {
                    if (rlpStream != null && item._data![i] is null)
                    {
                        int length = rlpStream.PeekNextRlpLength();
                        Span<byte> nextItem = rlpStream.Data.AsSpan().Slice(rlpStream.Position, length);
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
                        else if(item._data[i] is Keccak)
                        {
                            position = Rlp.Encode(destination, position, (item._data[i] as Keccak)!.Bytes);
                        }
                        else
                        {
                            TrieNode childNode = (TrieNode) item._data[i];
                            childNode!.ResolveKey(tree, false);
                            if (childNode.Keccak is null)
                            {
                                Span<byte> fullRlp = childNode.FullRlp.AsSpan();
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
