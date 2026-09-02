// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.History.Proofs;

internal static class BranchRlp
{
    public const int ChildCount = 16;
    private const int BranchItems = 17;
    private const byte EmptyItem = 0x80;

    public static bool IsBranch(ReadOnlySpan<byte> nodeRlp)
    {
        RlpReader reader = new(nodeRlp);
        int length = reader.ReadSequenceLength();
        return reader.PeekNumberOfItemsRemaining(reader.Position + length) == BranchItems;
    }

    public static void ReadChildren(ReadOnlySpan<byte> branchRlp, byte[]?[] children)
    {
        RlpReader reader = new(branchRlp);
        reader.ReadSequenceLength();
        for (int index = 0; index < ChildCount; index++)
        {
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            if (contentLength == 0)
            {
                children[index] = null;
                reader.SkipItem();
                continue;
            }

            if (!reader.IsSequenceNext() && contentLength == Hash256.Size)
            {
                reader.SkipBytes(prefixLength);
                children[index] = reader.Read(Hash256.Size).ToArray();
                continue;
            }

            children[index] = reader.Read(prefixLength + contentLength).ToArray();
        }

        if (reader.PeekPrefixAndContentLength().ContentLength != 0)
        {
            throw new InvalidDataException("A branch of a fixed-width-key trie carries no value; this node is not a state or storage trie branch.");
        }
    }

    public static byte[] Encode(byte[]?[] children)
    {
        int contentLength = 1;
        for (int index = 0; index < ChildCount; index++) contentLength += ItemLength(children[index]);

        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        for (int index = 0; index < ChildCount; index++)
        {
            byte[]? child = children[index];
            if (child is null)
            {
                rlp[position++] = EmptyItem;
            }
            else if (child.Length == Hash256.Size)
            {
                position += Rlp.Encode(child, rlp.AsSpan(position));
            }
            else
            {
                child.CopyTo(rlp.AsSpan(position));
                position += child.Length;
            }
        }

        rlp[position] = EmptyItem;
        return rlp;
    }

    public static byte[] ReferenceOf(byte[] nodeRlp) =>
        nodeRlp.Length < Hash256.Size ? nodeRlp : Keccak.Compute(nodeRlp).BytesToArray();

    private static int ItemLength(byte[]? child) =>
        child is null ? 1 : child.Length == Hash256.Size ? 1 + Hash256.Size : child.Length;
}
