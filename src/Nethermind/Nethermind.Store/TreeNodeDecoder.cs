/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Encoding;
using Newtonsoft.Json;

namespace Nethermind.Store
{
    public class TreeNodeDecoder
    {
        private Rlp RlpEncodeBranch(TrieNode item)
        {
            int valueRlpLength = Rlp.LengthOf(item.Value);
            int contentLength = valueRlpLength + GetChildrenRlpLength(item);
            int sequenceLength = Rlp.GetSequenceRlpLength(contentLength);
            byte[] result = new byte[sequenceLength];
            Span<byte> resultSpan = result.AsSpan();
            int position = Rlp.StartSequence(result, 0, contentLength);
            WriteChildrenRlp(item, resultSpan.Slice(position, contentLength - valueRlpLength));
            position = sequenceLength - valueRlpLength;
            Rlp.Encode(result, position, item.Value);
            return new Rlp(result);
        }

        public Rlp Encode(TrieNode item)
        {
            if (item == null)
            {
                return Rlp.OfEmptySequence;
            }

            Metrics.TreeNodeRlpEncodings++;
            if (item.IsLeaf)
            {
                return EncodeLeaf(item);
            }

            if (item.IsBranch)
            {
                return RlpEncodeBranch(item);
            }

            if (item.IsExtension)
            {
                return EncodeExtension(item);
            }

            throw new InvalidOperationException($"Unknown node type {item.NodeType}");
        }

        private static Rlp EncodeExtension(TrieNode item)
        {
            byte[] keyBytes = item.Key.ToBytes();
            TrieNode nodeRef = item.GetChild(0);
            nodeRef.ResolveKey(false);
            int contentLength = Rlp.LengthOf(keyBytes) + (nodeRef.Keccak == null ? nodeRef.FullRlp.Length : Rlp.LengthOfKeccakRlp);
            int totalLength = Rlp.LengthOfSequence(contentLength);
            RlpStream rlpStream = new RlpStream(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(keyBytes);
            if (nodeRef.Keccak == null)
            {
                // I think it can only happen if we have a short extension to a branch with a short extension as the only child?
                // so |
                // so |
                // so E - - - - - - - - - - - - - - - 
                // so |
                // so |
                rlpStream.Encode(nodeRef.FullRlp);
            }
            else
            {
                rlpStream.Encode(nodeRef.Keccak);
            }

            return new Rlp(rlpStream.Data);
        }

        private static Rlp EncodeLeaf(TrieNode item)
        {
            byte[] keyBytes = item.Key.ToBytes();
            int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOf(item.Value);
            int totalLength = Rlp.LengthOfSequence(contentLength);
            RlpStream rlpStream = new RlpStream(totalLength);
            rlpStream.StartSequence(contentLength);
            rlpStream.Encode(keyBytes);
            rlpStream.Encode(item.Value);
            return new Rlp(rlpStream.Data);
        }

        private int GetChildrenRlpLength(TrieNode item)
        {
            int totalLength = 0;
            item.InitData();

            for (int i = 0; i < 16; i++)
            {
                if (item.DecoderContext != null && item._data[i] == null)
                {
                    totalLength += item._lookupTable[i * 2 + 1];
                }
                else
                {
                    if (ReferenceEquals(item._data[i], TrieNode.NullNode) || item._data[i] == null)
                    {
                        totalLength++;
                    }
                    else
                    {
                        TrieNode childNode = (TrieNode) item._data[i];
                        childNode.ResolveKey(false);
                        totalLength += childNode.Keccak == null ? childNode.FullRlp.Length : Rlp.LengthOfKeccakRlp;
                    }
                }
            }

            return totalLength;
        }

        private void WriteChildrenRlp(TrieNode item, Span<byte> destination)
        {
            int position = 0;
            Rlp.DecoderContext context = item.DecoderContext;
            item.InitData();

            for (int i = 0; i < 16; i++)
            {
                if (context != null && item._data[i] == null)
                {
                    context.Position = item._lookupTable[i * 2];
                    Span<byte> nextItem = context.Read(item._lookupTable[i * 2 + 1]);
                    nextItem.CopyTo(destination.Slice(position, nextItem.Length));
                    position += nextItem.Length;
                }
                else
                {
                    if (ReferenceEquals(item._data[i], TrieNode.NullNode) || item._data[i] == null)
                    {
                        destination[position++] = 128;
                    }
                    else
                    {
                        TrieNode childNode = (TrieNode) item._data[i];
                        childNode.ResolveKey(false);
                        if (childNode.Keccak == null)
                        {
                            Span<byte> fullRlp = childNode.FullRlp.Bytes.AsSpan();
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