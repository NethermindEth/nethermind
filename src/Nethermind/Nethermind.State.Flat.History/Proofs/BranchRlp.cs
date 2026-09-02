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
            ReadOnlySpan<byte> reference = ReadChild(ref reader);
            children[index] = reference.IsEmpty ? null : reference.ToArray();
        }

        RequireNoValue(ref reader);
    }

    public static void ReadChildren(ReadOnlySpan<byte> branchRlp, ChildVector children)
    {
        RlpReader reader = new(branchRlp);
        reader.ReadSequenceLength();
        for (int index = 0; index < ChildCount; index++)
        {
            ReadOnlySpan<byte> reference = ReadChild(ref reader);
            if (reference.IsEmpty) children.Clear(index);
            else children.Set(index, reference);
        }

        RequireNoValue(ref reader);
    }

    public static byte[] Encode(byte[]?[] children)
    {
        int contentLength = 1;
        for (int index = 0; index < ChildCount; index++) contentLength += ItemLength(children[index]?.Length ?? 0);

        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        for (int index = 0; index < ChildCount; index++) position = WriteChild(rlp, position, children[index]);
        rlp[position] = EmptyItem;
        return rlp;
    }

    public static byte[] Encode(ChildVector children)
    {
        int contentLength = 1;
        for (int index = 0; index < ChildCount; index++) contentLength += ItemLength(children[index].Length);

        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        for (int index = 0; index < ChildCount; index++) position = WriteChild(rlp, position, children[index]);
        rlp[position] = EmptyItem;
        return rlp;
    }

    public static byte[] ReferenceOf(byte[] nodeRlp) =>
        nodeRlp.Length < Hash256.Size ? nodeRlp : Keccak.Compute(nodeRlp).BytesToArray();

    private static ReadOnlySpan<byte> ReadChild(ref RlpReader reader)
    {
        (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
        if (contentLength == 0)
        {
            reader.SkipItem();
            return ReadOnlySpan<byte>.Empty;
        }

        if (!reader.IsSequenceNext() && contentLength == Hash256.Size)
        {
            reader.SkipBytes(prefixLength);
            return reader.Read(Hash256.Size);
        }

        return reader.Read(prefixLength + contentLength);
    }

    private static void RequireNoValue(ref RlpReader reader)
    {
        if (reader.PeekPrefixAndContentLength().ContentLength != 0)
        {
            throw new InvalidDataException("A branch of a fixed-width-key trie carries no value; this node is not a state or storage trie branch.");
        }
    }

    private static int WriteChild(Span<byte> rlp, int position, ReadOnlySpan<byte> child)
    {
        if (child.IsEmpty)
        {
            rlp[position] = EmptyItem;
            return position + 1;
        }

        if (child.Length == Hash256.Size) return Rlp.Encode(rlp, position, child);

        child.CopyTo(rlp[position..]);
        return position + child.Length;
    }

    private static int ItemLength(int childLength) =>
        childLength == 0 ? 1 : childLength == Hash256.Size ? 1 + Hash256.Size : childLength;
}
