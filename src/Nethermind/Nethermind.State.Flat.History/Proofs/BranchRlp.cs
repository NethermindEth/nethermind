// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.Flat.History.Proofs;

internal static class BranchRlp
{
    public const int ChildCount = 16;
    private const int BranchItems = 17;
    private const byte EmptyItem = 0x80;

    public static bool IsBranch(ReadOnlySpan<byte> nodeRlp)
    {
        RlpReader reader = new RlpReader(nodeRlp).EnterSequence<OffFlag>(out _);
        return reader.CountItems<OffFlag>(int.MaxValue) == BranchItems;
    }

    public static void ReadChildren(ReadOnlySpan<byte> branchRlp, ChildVector children)
    {
        RlpReader reader = new RlpReader(branchRlp).EnterSequence<OffFlag>(out _);
        for (int index = 0; index < ChildCount; index++)
        {
            ReadOnlySpan<byte> reference = ReadChild(ref reader);
            if (reference.IsEmpty) children.Clear(index);
            else children.Set(index, reference);
        }

        RequireNoValue(ref reader);
    }

    public static bool TryReadChildren(ReadOnlySpan<byte> nodeRlp, ChildVector children)
    {
        RlpReader reader = new RlpReader(nodeRlp).EnterSequence<OffFlag>(out _);
        for (int index = 0; index < ChildCount; index++)
        {
            if (!reader.HasNext) return false;

            ReadOnlySpan<byte> reference = ReadChild(ref reader);
            if (reference.Length > Hash256.Size) return false;

            if (reference.IsEmpty) children.Clear(index);
            else children.Set(index, reference);
        }

        if (!reader.HasNext) return false;

        ReadOnlySpan<byte> value = reader.ReadEncodedItem<OffFlag>();
        return !reader.HasNext && IsEmptyItem(value);
    }

    public static bool TryReadChild(ReadOnlySpan<byte> nodeRlp, int index, out int referenceLength, out ValueHash256 hash)
    {
        hash = default;
        RlpReader reader = new RlpReader(nodeRlp).EnterSequence<OffFlag>(out _);
        if (reader.CountItems<OffFlag>(int.MaxValue) != BranchItems)
        {
            referenceLength = 0;
            return false;
        }

        for (int i = 0; i < index; i++)
        {
            reader.SkipItem<OffFlag>();
        }

        ReadOnlySpan<byte> reference = ReadChild(ref reader);
        referenceLength = reference.Length;
        if (referenceLength == Hash256.Size) hash = new ValueHash256(reference);
        return true;
    }

    public static int EncodedLength(ChildVector children) => Rlp.LengthOfSequence(ContentLength(children));

    public static int Encode(ChildVector children, Span<byte> rlp)
    {
        int contentLength = ContentLength(children);
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        for (int index = 0; index < ChildCount; index++) position = WriteChild(rlp, position, children[index]);
        rlp[position++] = EmptyItem;
        return position;
    }

    public static byte[] Encode(ChildVector children)
    {
        byte[] rlp = new byte[EncodedLength(children)];
        Encode(children, rlp);
        return rlp;
    }

    public static int ReferenceOf(ReadOnlySpan<byte> nodeRlp, Span<byte> destination)
    {
        if (nodeRlp.Length < Hash256.Size)
        {
            nodeRlp.CopyTo(destination);
            return nodeRlp.Length;
        }

        ValueKeccak.Compute(nodeRlp).Bytes.CopyTo(destination);
        return Hash256.Size;
    }

    private static int ContentLength(ChildVector children)
    {
        int contentLength = 1;
        for (int index = 0; index < ChildCount; index++) contentLength += ItemLength(children[index].Length);
        return contentLength;
    }

    /// <summary>Whether <paramref name="item"/> is an empty string or an empty list.</summary>
    public static bool IsEmptyItem(ReadOnlySpan<byte> item) => item.Length == 1 && item[0] >= EmptyItem;

    /// <summary>Whether <paramref name="item"/> is a 32-byte string, the reference of a hashed node.</summary>
    public static bool IsHashItem(ReadOnlySpan<byte> item) => item.Length == 1 + Hash256.Size && item[0] == EmptyItem + Hash256.Size;

    private static ReadOnlySpan<byte> ReadChild(ref RlpReader reader)
    {
        ReadOnlySpan<byte> item = reader.ReadEncodedItem<OffFlag>();
        if (IsEmptyItem(item)) return ReadOnlySpan<byte>.Empty;
        return IsHashItem(item) ? item[1..] : item;
    }

    private static void RequireNoValue(ref RlpReader reader)
    {
        if (!IsEmptyItem(reader.PeekNextItem()))
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
