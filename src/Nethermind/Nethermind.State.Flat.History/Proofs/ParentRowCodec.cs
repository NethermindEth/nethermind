// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;

namespace Nethermind.State.Flat.History.Proofs;

internal static class ParentRowCodec
{
    private const byte BranchKind = 0;
    private const byte WholeNodeKind = 1;
    private const byte EmptyKind = 2;
    private const int KindAndBlockLength = 1 + sizeof(ulong);
    private const int HeaderLength = KindAndBlockLength + sizeof(ushort) + sizeof(ushort);

    public const int MaxBranchRowLength = HeaderLength + BranchRlp.ChildCount * (1 + ChildVector.SlotSize);

    public static byte[] EncodeBranch(ulong lastBlock, ushort presence, ushort changed, byte[]?[] children)
    {
        int length = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1) length += 1 + (children[index]?.Length ?? 0);
        }

        byte[] row = new byte[length];
        WriteBranchHeader(row, lastBlock, presence, changed);
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;

            position = WriteChild(row, position, children[index]);
        }

        return row;
    }

    public static int EncodeBranch(ulong lastBlock, ushort presence, ushort changed, ChildVector children, Span<byte> row)
    {
        WriteBranchHeader(row, lastBlock, presence, changed);
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;

            position = WriteChild(row, position, children[index]);
        }

        return position;
    }

    public static byte[] EncodeWholeNode(ulong lastBlock, ReadOnlySpan<byte> nodeRlp)
    {
        byte[] row = new byte[KindAndBlockLength + nodeRlp.Length];
        EncodeWholeNode(lastBlock, nodeRlp, row);
        return row;
    }

    public static int EncodeWholeNode(ulong lastBlock, ReadOnlySpan<byte> nodeRlp, Span<byte> row)
    {
        row[0] = WholeNodeKind;
        BinaryPrimitives.WriteUInt64BigEndian(row[1..], lastBlock);
        nodeRlp.CopyTo(row[KindAndBlockLength..]);
        return KindAndBlockLength + nodeRlp.Length;
    }

    public static byte[] EncodeEmpty(ulong lastBlock)
    {
        byte[] row = new byte[KindAndBlockLength];
        EncodeEmpty(lastBlock, row);
        return row;
    }

    public static int EncodeEmpty(ulong lastBlock, Span<byte> row)
    {
        row[0] = EmptyKind;
        BinaryPrimitives.WriteUInt64BigEndian(row[1..], lastBlock);
        return KindAndBlockLength;
    }

    public static int WholeNodeRowLength(int nodeRlpLength) => KindAndBlockLength + nodeRlpLength;

    public static bool IsBranchRow(ReadOnlySpan<byte> row) => row.Length >= HeaderLength && row[0] == BranchKind && PayloadIsConsistent(row);

    public static bool IsWholeNodeRow(ReadOnlySpan<byte> row) => row.Length > KindAndBlockLength && row[0] == WholeNodeKind;

    public static bool IsEmptyRow(ReadOnlySpan<byte> row) => row.Length == KindAndBlockLength && row[0] == EmptyKind;

    public static bool IsValid(ReadOnlySpan<byte> row) => IsBranchRow(row) || IsWholeNodeRow(row) || IsEmptyRow(row);

    public static ReadOnlySpan<byte> WholeNodeRlp(ReadOnlySpan<byte> row) => row[KindAndBlockLength..];

    public static ulong LastBlock(ReadOnlySpan<byte> row) => BinaryPrimitives.ReadUInt64BigEndian(row[1..]);

    public static ushort Presence(ReadOnlySpan<byte> row) => BinaryPrimitives.ReadUInt16BigEndian(row[9..]);

    public static ushort Changed(ReadOnlySpan<byte> row) => BinaryPrimitives.ReadUInt16BigEndian(row[11..]);

    public static ushort Fill(ReadOnlySpan<byte> row, ushort wanted, byte[]?[] children)
    {
        ushort changed = Changed(row);
        ushort filled = 0;
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;

            int length = row[position++];
            if (((wanted >> index) & 1) == 1 && children[index] is null)
            {
                children[index] = length == 0 ? null : row.Slice(position, length).ToArray();
                filled |= (ushort)(1 << index);
            }

            position += length;
        }

        return filled;
    }

    public static ushort Fill(ReadOnlySpan<byte> row, ushort wanted, ChildVector children)
    {
        ushort changed = Changed(row);
        ushort filled = 0;
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;

            int length = row[position++];
            if (((wanted >> index) & 1) == 1 && !children.IsPresent(index))
            {
                if (length != 0) children.Set(index, row.Slice(position, length));
                filled |= (ushort)(1 << index);
            }

            position += length;
        }

        return filled;
    }

    private static void WriteBranchHeader(Span<byte> row, ulong lastBlock, ushort presence, ushort changed)
    {
        row[0] = BranchKind;
        BinaryPrimitives.WriteUInt64BigEndian(row[1..], lastBlock);
        BinaryPrimitives.WriteUInt16BigEndian(row[9..], presence);
        BinaryPrimitives.WriteUInt16BigEndian(row[11..], changed);
    }

    private static int WriteChild(Span<byte> row, int position, ReadOnlySpan<byte> child)
    {
        row[position++] = (byte)child.Length;
        child.CopyTo(row[position..]);
        return position + child.Length;
    }

    private static bool PayloadIsConsistent(ReadOnlySpan<byte> row)
    {
        ushort changed = Changed(row);
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;
            if (position >= row.Length || row[position] > ChildVector.SlotSize) return false;

            position += 1 + row[position];
        }

        return position == row.Length;
    }
}
