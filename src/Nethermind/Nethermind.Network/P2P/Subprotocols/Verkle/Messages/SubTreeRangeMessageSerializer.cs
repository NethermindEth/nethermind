// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using DotNetty.Buffers;
using Nethermind.Serialization.Rlp;
using Nethermind.Verkle.Tree.Sync;

namespace Nethermind.Network.P2P.Subprotocols.Verkle.Messages;

public class SubTreeRangeMessageSerializer : IZeroMessageSerializer<SubTreeRangeMessage>
{

    private readonly Func<RlpStream, PathWithSubTree> _decodeSubTree;
    private readonly Func<RlpStream, LeafInSubTree> _decodeLeaf;

    public SubTreeRangeMessageSerializer()
    {
        _decodeSubTree = DecodePathWithRlpData;
        _decodeLeaf = DecodeLeafSubTree;
    }
    public void Serialize(IByteBuffer byteBuffer, SubTreeRangeMessage message)
    {
        (int contentLength, int pwasLength) = GetLength(message);

        byteBuffer.EnsureWritable(Rlp.LengthOfSequence(contentLength), true);

        NettyRlpStream stream = new(byteBuffer);
        stream.StartSequence(contentLength);

        stream.Encode(message.RequestId);
        if (message.PathsWithSubTrees is null || message.PathsWithSubTrees.Length == 0)
        {
            stream.EncodeNullObject();
        }
        else
        {
            stream.StartSequence(pwasLength);
            for (int i = 0; i < message.PathsWithSubTrees.Length; i++)
            {
                PathWithSubTree pwa = message.PathsWithSubTrees[i];

                int subTreeLength = 0;
                foreach (LeafInSubTree t in pwa.SubTree)
                {
                    subTreeLength += Rlp.LengthOfSequence(Rlp.LengthOf(t.SuffixByte) + Rlp.LengthOf(t.Leaf));
                }

                int pwaLength = Rlp.LengthOf(pwa.Path.Bytes) + Rlp.LengthOfSequence(subTreeLength);

                stream.StartSequence(pwaLength);
                stream.Encode(pwa.Path.Bytes);
                stream.StartSequence(subTreeLength);
                foreach (LeafInSubTree leaf in pwa.SubTree)
                {
                    stream.StartSequence(Rlp.LengthOf(leaf.SuffixByte) + Rlp.LengthOf(leaf.Leaf));
                    stream.Encode(leaf.SuffixByte);
                    stream.Encode(leaf.Leaf);
                }
            }
        }

        if (message.Proofs is null || message.Proofs.Length == 0)
        {
            stream.EncodeEmptyByteArray();
        }
        else
        {
            stream.Encode(message.Proofs);
        }
    }

    public SubTreeRangeMessage Deserialize(IByteBuffer byteBuffer)
    {
        SubTreeRangeMessage message = new();
        NettyRlpStream rlpStream = new(byteBuffer);

        rlpStream.ReadSequenceLength();

        message.RequestId = rlpStream.DecodeLong();
        message.PathsWithSubTrees = rlpStream.DecodeArray(_decodeSubTree);
        message.Proofs = rlpStream.DecodeByteArray();

        return message;
    }

    private PathWithSubTree DecodePathWithRlpData(RlpStream stream)
    {
        stream.ReadSequenceLength();
        byte[] path = stream.DecodeByteArray();
        LeafInSubTree[]? subTrees = stream.DecodeArray(_decodeLeaf); ;
        PathWithSubTree data = new(path, subTrees);
        return data;
    }

    private static LeafInSubTree DecodeLeafSubTree(RlpStream stream)
    {
        stream.ReadSequenceLength();
        byte suffix = stream.DecodeByte();
        byte[]? leaf = stream.DecodeByteArray();
        return new LeafInSubTree(suffix, leaf);
    }

    private (int contentLength, int pwasLength) GetLength(SubTreeRangeMessage message)
    {
        int contentLength = Rlp.LengthOf(message.RequestId);

        int pwasLength = 0;
        if (message.PathsWithSubTrees is null || message.PathsWithSubTrees.Length == 0)
        {
            pwasLength = 1;
        }
        else
        {
            for (int i = 0; i < message.PathsWithSubTrees.Length; i++)
            {
                PathWithSubTree pwa = message.PathsWithSubTrees[i];
                int subTreeLength = 0;
                foreach (LeafInSubTree t in pwa.SubTree)
                {
                    subTreeLength += Rlp.LengthOfSequence(Rlp.LengthOf(t.SuffixByte) + Rlp.LengthOf(t.Leaf));
                }
                int pwaLength = Rlp.LengthOf(pwa.Path.Bytes) + Rlp.LengthOfSequence(subTreeLength);

                pwasLength += Rlp.LengthOfSequence(pwaLength);
            }
        }

        contentLength += Rlp.LengthOfSequence(pwasLength);

        int proofsLength = (message.Proofs is null || message.Proofs.Length == 0) ? 1 : Rlp.LengthOf(message.Proofs);

        contentLength += proofsLength;

        return (contentLength, pwasLength);
    }
}
