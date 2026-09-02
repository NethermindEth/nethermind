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

    public static byte[] EncodeBranch(ulong lastBlock, ushort presence, ushort changed, byte[]?[] children)
    {
        int length = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 1) length += 1 + (children[index]?.Length ?? 0);
        }

        byte[] row = new byte[length];
        row[0] = BranchKind;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(1), lastBlock);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(9), presence);
        BinaryPrimitives.WriteUInt16BigEndian(row.AsSpan(11), changed);
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;

            byte[]? child = children[index];
            row[position++] = (byte)(child?.Length ?? 0);
            if (child is null) continue;

            child.CopyTo(row.AsSpan(position));
            position += child.Length;
        }

        return row;
    }

    public static byte[] EncodeWholeNode(ulong lastBlock, ReadOnlySpan<byte> nodeRlp)
    {
        byte[] row = new byte[KindAndBlockLength + nodeRlp.Length];
        row[0] = WholeNodeKind;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(1), lastBlock);
        nodeRlp.CopyTo(row.AsSpan(KindAndBlockLength));
        return row;
    }

    public static byte[] EncodeEmpty(ulong lastBlock)
    {
        byte[] row = new byte[KindAndBlockLength];
        row[0] = EmptyKind;
        BinaryPrimitives.WriteUInt64BigEndian(row.AsSpan(1), lastBlock);
        return row;
    }

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

    private static bool PayloadIsConsistent(ReadOnlySpan<byte> row)
    {
        ushort changed = Changed(row);
        int position = HeaderLength;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (((changed >> index) & 1) == 0) continue;
            if (position >= row.Length) return false;

            position += 1 + row[position];
        }

        return position == row.Length;
    }
}
